import {API_URL} from "./config.js";
import {getToken, refreshToken, signOut} from "./auth.js";
import {authorizationHeader, devUserOf} from "./authorization.js";
import {parsePacket, parsePackets, toLiveEvent} from "./packets.js";
import {ServerSentEventParser} from "./sse.js";
import type {
    ApiResult,
    DeletePacketResponse,
    GetPacketsResponse,
    LiveEvent,
    PacketData,
    ProblemDetails,
    SendPacketResponse,
} from "./types.js";

export class SignedOutError extends Error {
}

/** A failed API call; the message is user-facing. */
class ApiError extends Error {
}

const OFFLINE_MESSAGE = "Couldn't reach Passel. Check your connection and try again.";

/** How long the event stream may stay silent before it's treated as dead (the server sends a heartbeat every 20s). */
const EVENT_STREAM_IDLE_TIMEOUT_MS = 45_000;

/**
 * Builds a URL under the packets endpoint. API_URL is the endpoint itself and may carry query parameters
 * (e.g. api-version), which are kept.
 */
function endpoint(path = "", params: Record<string, string> = {}): string {
    const url = new URL(API_URL);
    if (path) url.pathname = `${url.pathname.replace(/\/$/, "")}/${path}`;
    for (const [key, value] of Object.entries(params)) url.searchParams.set(key, value);
    return url.toString();
}

/**
 * Fetches with the user's ID token. On a 401 the token is refreshed (silently first) and the
 * request retried once; if that still fails the user is signed out. `interactive` allows a
 * sign-in window to be shown and should only be true for user-initiated actions.
 */
async function authenticatedFetch(url: string, options: RequestInit, interactive: boolean): Promise<Response> {
    let token = await getToken(interactive);
    if (!token) throw new SignedOutError("You're not signed in.");

    let response = await fetchWithToken(url, options, token);
    if (response.status !== 401) return response;

    // A development token can't be refreshed: the API refuses it when it isn't running in Development.
    if (devUserOf(token) !== null) {
        await signOut();
        throw new SignedOutError("The API refused the development user. Is it running in Development?");
    }

    token = await refreshToken(interactive);
    if (token) {
        response = await fetchWithToken(url, options, token);
        if (response.status !== 401) return response;
    }

    await signOut();
    throw new SignedOutError("Your session has expired. Please sign in again.");
}

function fetchWithToken(url: string, options: RequestInit, token: string): Promise<Response> {
    const headers = new Headers(options.headers);
    headers.set("Authorization", authorizationHeader(token));
    return fetch(url, {...options, headers});
}

/** Runs an API call, turning failures into an ApiResult with a user-facing message. */
async function apiCall<R extends ApiResult>(description: string, call: () => Promise<R>): Promise<R | ApiResult> {
    try {
        return await call();
    } catch (err) {
        if (err instanceof SignedOutError) return {success: false, error: err.message, signedOut: true};
        if (err instanceof ApiError) return {success: false, error: err.message};

        console.error(`Passel: ${description} failed`, err);
        return {success: false, error: OFFLINE_MESSAGE};
    }
}

async function ensureOk(response: Response): Promise<void> {
    if (response.ok) return;

    const error = await readError(response);
    console.error(`Passel: API returned ${response.status}: ${error}`);
    throw new ApiError(error);
}

export function sendPacket(data: PacketData, interactive: boolean): Promise<SendPacketResponse> {
    return apiCall("sending a packet", async () => {
        const response = await authenticatedFetch(endpoint(), {
            method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(data),
        }, interactive);
        await ensureOk(response);

        // The send succeeded even if the echoed packet can't be parsed; the feed then picks it up on its next sync.
        const packet = parsePacket(await response.json().catch(() => null));
        return {success: true, packet: packet ?? undefined};
    });
}

/** Fetches a page of packets, newest first; omit `cursor` for the first page. Never shows a sign-in window. */
export function getPackets(cursor?: string): Promise<GetPacketsResponse> {
    return apiCall("loading packets", async () => {
        const response = await authenticatedFetch(endpoint("", cursor ? {cursor} : {}), {}, false);
        await ensureOk(response);

        const body = await response.json() as { packets?: unknown; nextCursor?: unknown };
        return {
            success: true,
            page: {
                packets: parsePackets(body.packets),
                nextCursor: typeof body.nextCursor === "string" ? body.nextCursor : null,
            },
        };
    });
}

/** Deletes a packet. One that no longer exists (e.g. deleted on another device) counts as deleted. */
export function deletePacket(id: string, interactive: boolean): Promise<DeletePacketResponse> {
    return apiCall("deleting a packet", async () => {
        const response = await authenticatedFetch(endpoint(encodeURIComponent(id)), {method: "DELETE"}, interactive);
        if (response.status !== 404) await ensureOk(response);
        return {success: true};
    });
}

/**
 * Opens the live event stream and calls `onEvent` for each event until the stream ends (resolves),
 * fails (rejects; SignedOutError if the session can't be used) or `signal` is aborted.
 * Never shows a sign-in window.
 */
export async function watchEvents(signal: AbortSignal, onEvent: (event: LiveEvent) => void): Promise<void> {
    const response = await authenticatedFetch(endpoint("events"), {
        headers: {Accept: "text/event-stream"}, signal,
    }, false);
    await ensureOk(response);

    const contentType = response.headers.get("Content-Type") ?? "";
    if (!contentType.startsWith("text/event-stream") || !response.body) {
        throw new Error(`Expected an event stream but got "${contentType}"`);
    }

    const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
    const parser = new ServerSentEventParser();

    try {
        while (true) {
            const {value, done} = await readWithTimeout(reader, EVENT_STREAM_IDLE_TIMEOUT_MS);
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

function readWithTimeout<T>(reader: ReadableStreamDefaultReader<T>, timeoutMs: number): Promise<ReadableStreamReadResult<T>> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const timeout = new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error("The event stream went silent")), timeoutMs);
    });
    return Promise.race([reader.read(), timeout]).finally(() => clearTimeout(timer));
}

/** Builds a user-facing message from a ProblemDetails body, falling back to the status code. */
async function readError(response: Response): Promise<string> {
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
