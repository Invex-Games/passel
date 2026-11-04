import {describe, expect, it} from "vitest";
import {ApiError, errorMessage, OFFLINE_MESSAGE, PacketsApi, readError, SignedOutError} from "../src/api.js";
import {devToken} from "../src/authorization.js";
import {FeedRequestError} from "../src/feed.js";
import type {LiveEvent} from "../src/types.js";

const ENDPOINT = "https://api.test/api/packets?api-version=1";

interface Call {
    url: string;
    method: string;
    authorization: string | null;
    body: string | null;
}

/** A PacketsApi whose fetch answers from `respond` and records every call. */
function api(respond: (call: Call, index: number) => Response | Promise<Response>, options: {
    token?: string | null;
    renewToken?: (rejected: string) => Promise<string | null>;
    idleTimeoutMs?: number;
} = {}) {
    const calls: Call[] = [];
    const client = new PacketsApi({
        endpoint: ENDPOINT,
        getToken: () => options.token === undefined ? "token-1" : options.token,
        renewToken: options.renewToken,
        eventStreamIdleTimeoutMs: options.idleTimeoutMs,
        fetch: async (url, init) => {
            const headers = new Headers(init?.headers);
            const call = {
                url,
                method: init?.method ?? "GET",
                authorization: headers.get("Authorization"),
                body: typeof init?.body === "string" ? init.body : null,
            };
            calls.push(call);
            return respond(call, calls.length - 1);
        },
    });
    return {client, calls};
}

const json = (body: unknown, status = 200, contentType = "application/json") =>
    new Response(JSON.stringify(body), {status, headers: {"Content-Type": contentType}});

const problem = (status: number, body: Record<string, unknown>) => json(body, status, "application/problem+json");

const textPacket = {$type: "Text", id: "1", createdAt: "2026-10-22T11:02:08Z", title: null, body: "hi"};

describe("requests", () => {
    it("lists a page with the bearer token, keeping the endpoint's query and adding the cursor", async () => {
        const {client, calls} = api(() => json({packets: [textPacket, {$type: "Video", id: "2"}], nextCursor: "c1"}));

        const page = await client.getPage("c0");

        expect(page).toEqual({packets: [{id: "1", type: "text", title: null, payload: "hi", isImage: false}], nextCursor: "c1"});
        expect(calls).toEqual([{
            url: "https://api.test/api/packets?api-version=1&cursor=c0",
            method: "GET",
            authorization: "Bearer token-1",
            body: null,
        }]);
    });

    it("sends a development user as the Dev scheme", async () => {
        const {client, calls} = api(() => json({packets: [], nextCursor: null}), {token: devToken("alice")});

        await client.getPage();

        expect(calls[0].authorization).toBe("Dev alice");
        expect(calls[0].url).toBe(ENDPOINT);
    });

    it("sends a packet and returns the echo, or null when the echo doesn't parse", async () => {
        const {client, calls} = api((_, i) => i === 0 ? json(textPacket, 201) : new Response("not json", {status: 201}));

        expect(await client.send({$type: "Text", title: null, body: "hi"})).toMatchObject({id: "1", type: "text"});
        expect(await client.send({$type: "Text", title: null, body: "hi"})).toBeNull();
        expect(calls[0]).toMatchObject({method: "POST", body: '{"$type":"Text","title":null,"body":"hi"}'});
    });

    it("deletes by id, counting a packet that is already gone as deleted", async () => {
        const {client, calls} = api((_, i) => new Response(null, {status: i === 0 ? 204 : 404}));

        await client.delete("a/b");
        await client.delete("gone");

        expect(calls.map(c => [c.method, c.url])).toEqual([
            ["DELETE", "https://api.test/api/packets/a%2Fb?api-version=1"],
            ["DELETE", "https://api.test/api/packets/gone?api-version=1"],
        ]);
    });
});

describe("failures", () => {
    it("is signed out without a token, and sends nothing", async () => {
        const {client, calls} = api(() => json({}), {token: null});

        await expect(client.getPage()).rejects.toThrow(SignedOutError);
        expect(calls).toEqual([]);
    });

    it("retries a 401 once with a renewed token", async () => {
        const rejected: string[] = [];
        const {client, calls} = api((_, i) => i === 0 ? new Response(null, {status: 401}) : json({packets: [], nextCursor: null}), {
            renewToken: async token => {
                rejected.push(token);
                return "token-2";
            },
        });

        await client.getPage();

        expect(rejected).toEqual(["token-1"]);
        expect(calls.map(c => c.authorization)).toEqual(["Bearer token-1", "Bearer token-2"]);
    });

    it("is signed out when the token can't be renewed or the renewed one is refused too", async () => {
        const unauthorized = () => new Response(null, {status: 401});

        await expect(api(unauthorized).client.getPage()).rejects.toThrow("Your session has expired");

        const {client, calls} = api(unauthorized, {renewToken: async () => "token-2"});
        await expect(client.getPage()).rejects.toThrow(SignedOutError);
        expect(calls).toHaveLength(2);
    });

    it("never renews a development token", async () => {
        let renewed = false;
        const {client, calls} = api(() => new Response(null, {status: 401}), {
            token: devToken("alice"),
            renewToken: async () => {
                renewed = true;
                return "token-2";
            },
        });

        await expect(client.getPage()).rejects.toThrow("refused the development user");
        expect(renewed).toBe(false);
        expect(calls).toHaveLength(1);
    });

    it("turns a problem into its message and keeps the status", async () => {
        const {client} = api(() => problem(429, {title: "Too many requests.", detail: "Try again in a minute."}));

        const error = await client.getPage().catch(err => err);

        expect(error).toBeInstanceOf(ApiError);
        expect(error.message).toBe("Too many requests. Try again in a minute.");
        expect(error.status).toBe(429);
    });

    it("gives the feed a FeedRequestError that says whether the user is signed out", async () => {
        const offline = api(() => Promise.reject(new TypeError("Failed to fetch"))).client.feedSource();
        const signedOut = api(() => json({}), {token: null}).client.feedSource();

        const offlineError = await offline.fetchPage().catch(err => err);
        const signedOutError = await signedOut.fetchPage().catch(err => err);

        expect(offlineError).toBeInstanceOf(FeedRequestError);
        expect(offlineError).toMatchObject({message: OFFLINE_MESSAGE, signedOut: false});
        expect(signedOutError).toMatchObject({message: "You're not signed in.", signedOut: true});
    });
});

describe("readError", () => {
    it("joins title and detail, or falls back to the validation errors and then the status", async () => {
        expect(await readError(problem(400, {title: "Invalid packet", errors: {url: ["Must be http(s)."], name: ["Too long."]}})))
            .toBe("Invalid packet: Must be http(s). Too long.");
        expect(await readError(problem(500, {title: "Server error"}))).toBe("Server error");
        expect(await readError(problem(500, {}))).toBe("Request failed (500).");
        expect(await readError(new Response("<html>", {status: 502, headers: {"Content-Type": "text/html"}})))
            .toBe("Request failed (502).");
        expect(await readError(new Response("{oops", {status: 500, headers: {"Content-Type": "application/json"}})))
            .toBe("Request failed (500).");
    });

    it("errorMessage shows the API's message and hides anything else behind the offline message", () => {
        expect(errorMessage(new ApiError("Nope.", 400))).toBe("Nope.");
        expect(errorMessage(new SignedOutError("Sign in."))).toBe("Sign in.");
        expect(errorMessage(new FeedRequestError("Too many requests."))).toBe("Too many requests.");
        expect(errorMessage(new TypeError("Failed to fetch"))).toBe(OFFLINE_MESSAGE);
    });
});

describe("watchEvents", () => {
    /** An event stream response whose body the test writes chunk by chunk. */
    function stream() {
        let controller!: ReadableStreamDefaultController<Uint8Array>;
        const body = new ReadableStream<Uint8Array>({start: c => void (controller = c)});
        const encoder = new TextEncoder();
        return {
            response: new Response(body, {headers: {"Content-Type": "text/event-stream"}}),
            write: (text: string) => controller.enqueue(encoder.encode(text)),
            end: () => controller.close(),
        };
    }

    it("parses the stream into live events until it ends", async () => {
        const events: LiveEvent[] = [];
        const body = stream();
        const {client, calls} = api(() => body.response);

        const watching = client.watchEvents(new AbortController().signal, event => events.push(event));
        body.write("retry: 3000\nevent: resync\ndata: \n\n: comment\n");
        body.write(`event: packet-created\ndata: ${JSON.stringify(textPacket)}\n\nevent: ping\ndata: \n\n`);
        body.write('event: packet-deleted\ndata: {"id":"1"}\n\n');
        body.end();
        await watching;

        expect(calls[0].url).toBe("https://api.test/api/packets/events?api-version=1");
        expect(events.map(e => e.kind)).toEqual(["resync", "created", "deleted"]);
    });

    it("refuses an answer that isn't an event stream", async () => {
        const {client} = api(() => json({}));

        await expect(client.watchEvents(new AbortController().signal, () => {
        })).rejects.toThrow("Expected an event stream");
    });

    it("gives up on a stream that goes silent", async () => {
        const body = stream();
        const {client} = api(() => body.response, {idleTimeoutMs: 10});

        await expect(client.watchEvents(new AbortController().signal, () => {
        })).rejects.toThrow("went silent");
    });
});
