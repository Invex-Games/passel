import {authorizationHeader, devUserOf} from "./authorization.js";
import {FeedRequestError, type FeedSource} from "./feed.js";
import {parsePacket, parsePackets, toLiveEvent} from "./packets.js";
import {ServerSentEventParser} from "./sse.js";
import type {LiveEvent, Packet, PacketData, PacketPage, ProblemDetails} from "./types.js";

// A client for the packet endpoints over plain fetch: listing, sending, deleting and the live event stream, with the
// Authorization header and the API's problem bodies handled in one place. It holds no token itself; the caller
// supplies one per request, so the web app can keep its session wherever it likes. Nothing here is specific to a
// browser tab or an extension, so it runs under the tests as it is.

/** The message for a request that never got an answer. */
export const OFFLINE_MESSAGE = "Couldn't reach Passel. Check your connection and try again.";

/** How long the event stream may stay silent before it's treated as dead (the server sends a heartbeat every 20s). */
export const EVENT_STREAM_IDLE_TIMEOUT_MS = 45_000;

/** A request failed because there is no usable session; the user needs to sign in (again). */
export class SignedOutError extends Error {
}

/** The API answered with an error; the message is user-facing. */
export class ApiError extends Error {
    constructor(message: string, readonly status: number) {
        super(message);
    }
}

export interface PacketsApiOptions {
    /** The packets endpoint (for example `https://api.passel.link/api/packets`), not the API root. */
    endpoint: string;

    /** The token to send, or null when nobody is signed in. Called for every request. */
    getToken(): string | null | Promise<string | null>;

    /**
     * Called when the API refused `rejected` with a 401: returns a token to retry the request with, once, or null
     * to give up. Without it, a 401 ends the request at once.
     */
    renewToken?(rejected: string): Promise<string | null>;

    /** Defaults to the global fetch. */
    fetch?: (input: string, init?: RequestInit) => Promise<Response>;

    /** Defaults to EVENT_STREAM_IDLE_TIMEOUT_MS. */
    eventStreamIdleTimeoutMs?: number;
}

export class PacketsApi {
    private readonly fetch: (input: string, init?: RequestInit) => Promise<Response>;

    constructor(private readonly options: PacketsApiOptions) {
        // Called through a wrapper: a browser's fetch throws when it's called detached from the window
        this.fetch = options.fetch ?? ((input, init) => globalThis.fetch(input, init));
    }

    /** Fetches a page of packets, newest first; omit `cursor` for the first page. */
    async getPage(cursor?: string): Promise<PacketPage> {
        const response = await this.authenticatedFetch(this.url("", cursor ? {cursor} : {}), {});
        await ensureOk(response);

        const body = await response.json() as { packets?: unknown; nextCursor?: unknown };
        return {
            packets: parsePackets(body.packets),
            nextCursor: typeof body.nextCursor === "string" ? body.nextCursor : null,
        };
    }

    /**
     * Sends a packet. Returns the created packet, or null when the echo can't be parsed: the send succeeded anyway,
     * and the feed picks the packet up on its next sync.
     */
    async send(data: PacketData): Promise<Packet | null> {
        const response = await this.authenticatedFetch(this.url(), {
            method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(data),
        });
        await ensureOk(response);
        return parsePacket(await response.json().catch(() => null));
    }

    /** Deletes a packet. One that no longer exists (deleted on another device) counts as deleted. */
    async delete(id: string): Promise<void> {
        const response = await this.authenticatedFetch(this.url(encodeURIComponent(id)), {method: "DELETE"});
        if (response.status !== 404) await ensureOk(response);
    }

    /**
     * Opens the live event stream and calls `onEvent` for each event until the stream ends (resolves), fails
     * (rejects; SignedOutError when the session can't be used) or `signal` is aborted.
     */
    async watchEvents(signal: AbortSignal, onEvent: (event: LiveEvent) => void): Promise<void> {
        const response = await this.authenticatedFetch(this.url("events"), {
            headers: {Accept: "text/event-stream"}, signal,
        });
        await ensureOk(response);

        const contentType = response.headers.get("Content-Type") ?? "";
        if (!contentType.startsWith("text/event-stream") || !response.body) {
            throw new Error(`Expected an event stream but got "${contentType}"`);
        }

        const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
        const parser = new ServerSentEventParser();
        const idleTimeoutMs = this.options.eventStreamIdleTimeoutMs ?? EVENT_STREAM_IDLE_TIMEOUT_MS;

        try {
            while (true) {
                const {value, done} = await readWithTimeout(reader, idleTimeoutMs);
                if (done) return;

                for (const event of parser.push(value)) {
                    const liveEvent = toLiveEvent(event.event, event.data);
                    if (liveEvent) onEvent(liveEvent);
                }
            }
        } finally {
            reader.cancel().catch(() => {
            });
        }
    }

    /** The feed's packet source: getPage, with failures turned into FeedRequestError with a user-facing message. */
    feedSource(): FeedSource {
        return {
            fetchPage: async cursor => {
                try {
                    return await this.getPage(cursor);
                } catch (err) {
                    throw new FeedRequestError(errorMessage(err), err instanceof SignedOutError);
                }
            },
        };
    }

    // ── Private ──────────────────────────────────────────

    /**
     * Builds a URL under the packets endpoint. The endpoint may carry query parameters (an api-version), which
     * are kept.
     */
    private url(path = "", params: Record<string, string> = {}): string {
        const url = new URL(this.options.endpoint);
        if (path) url.pathname = `${url.pathname.replace(/\/$/, "")}/${path}`;
        for (const [key, value] of Object.entries(params)) url.searchParams.set(key, value);
        return url.toString();
    }

    /** Fetches with the current token; on a 401, retries once with a renewed token if renewToken gives one. */
    private async authenticatedFetch(url: string, init: RequestInit): Promise<Response> {
        const token = await this.options.getToken();
        if (!token) throw new SignedOutError("You're not signed in.");

        let response = await this.fetchWithToken(url, init, token);
        if (response.status !== 401) return response;

        // A development token can't be renewed: the API refuses it when it isn't running in Development
        if (devUserOf(token) !== null) {
            throw new SignedOutError("The API refused the development user. Is it running in Development?");
        }

        const renewed = await this.options.renewToken?.(token) ?? null;
        if (renewed) {
            response = await this.fetchWithToken(url, init, renewed);
            if (response.status !== 401) return response;
        }

        throw new SignedOutError("Your session has expired. Please sign in again.");
    }

    private fetchWithToken(url: string, init: RequestInit, token: string): Promise<Response> {
        const headers = new Headers(init.headers);
        headers.set("Authorization", authorizationHeader(token));
        return this.fetch(url, {...init, headers});
    }
}

/**
 * The user-facing message for a failed call: the API's own (or the feed's, which is already user-facing), or
 * OFFLINE_MESSAGE when it never answered.
 */
export function errorMessage(err: unknown): string {
    return err instanceof ApiError || err instanceof SignedOutError || err instanceof FeedRequestError
        ? err.message
        : OFFLINE_MESSAGE;
}

async function ensureOk(response: Response): Promise<void> {
    if (response.ok) return;
    throw new ApiError(await readError(response), response.status);
}

/** Builds a user-facing message from a problem details body, falling back to the status code. */
export async function readError(response: Response): Promise<string> {
    const fallback = `Request failed (${response.status}).`;

    try {
        const contentType = response.headers.get("Content-Type") ?? "";
        if (!contentType.includes("json")) return fallback;

        const problem = await response.json() as ProblemDetails;
        const validationErrors = problem.errors ? Object.values(problem.errors).flat().join(" ") : "";
        const title = problem.title?.trim();
        const detail = (problem.detail || validationErrors).trim();

        if (title && detail) return /[.!?]$/.test(title) ? `${title} ${detail}` : `${title}: ${detail}`;
        return title || detail || fallback;
    } catch {
        return fallback;
    }
}

function readWithTimeout<T>(reader: ReadableStreamDefaultReader<T>, timeoutMs: number): Promise<ReadableStreamReadResult<T>> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const timeout = new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error("The event stream went silent")), timeoutMs);
    });
    return Promise.race([reader.read(), timeout]).finally(() => clearTimeout(timer));
}
