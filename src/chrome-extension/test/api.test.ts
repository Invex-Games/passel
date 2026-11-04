import {afterEach, beforeEach, describe, expect, it, vi} from "vitest";
import type {LiveEvent, PacketData} from "@passel/core";

const auth = vi.hoisted(() => ({
    getToken: vi.fn<(interactive: boolean) => Promise<string | null>>(),
    refreshToken: vi.fn<(interactive: boolean) => Promise<string | null>>(),
    signOut: vi.fn<() => Promise<void>>(),
}));
vi.mock("../src/auth.js", () => auth);

import {deletePacket, getPackets, sendPacket, SignedOutError, watchEvents} from "../src/api.js";

// test/setup.ts sets API_URL to the packets endpoint with an api-version query parameter, which every URL keeps.
const PACKETS = "https://api.passel.test/api/packets";

const NOTE: PacketData = {$type: "Text", title: null, body: "Milk"};
const OFFLINE_MESSAGE = "Couldn't reach Passel. Check your connection and try again.";

const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>();

beforeEach(() => {
    vi.stubGlobal("fetch", fetchMock);
    auth.getToken.mockResolvedValue("token-1");
    auth.refreshToken.mockResolvedValue(null);
    auth.signOut.mockResolvedValue();
    vi.spyOn(console, "error").mockImplementation(() => {
    });
});

afterEach(() => {
    vi.useRealTimers();
});

function json(body: unknown, status = 200, contentType = "application/json"): Response {
    return new Response(JSON.stringify(body), {status, headers: {"Content-Type": contentType}});
}

function status(code: number): Response {
    return new Response(null, {status: code});
}

/** Answers each fetch with the next of these responses. */
function respond(...responses: Response[]): void {
    for (const response of responses) fetchMock.mockResolvedValueOnce(response);
}

function authorizationOf(call: number): string | null {
    return new Headers(fetchMock.mock.calls[call][1]?.headers).get("Authorization");
}

function urlOf(call: number): URL {
    return new URL(fetchMock.mock.calls[call][0]);
}

describe("authenticated requests: the 401 matrix", () => {
    it("sends the token as a Bearer token", async () => {
        respond(json({$type: "Text", id: "p1", title: null, body: "Milk"}, 201));

        expect(await sendPacket(NOTE, true)).toEqual({
            success: true, packet: {id: "p1", type: "text", title: null, payload: "Milk", isImage: false},
        });
        expect(authorizationOf(0)).toBe("Bearer token-1");
        expect(auth.refreshToken).not.toHaveBeenCalled();
    });

    it("without a token, answers signed out and sends nothing", async () => {
        auth.getToken.mockResolvedValue(null);

        expect(await getPackets()).toEqual({success: false, error: "You're not signed in.", signedOut: true});
        expect(fetchMock).not.toHaveBeenCalled();
    });

    it("on a 401, refreshes the token and retries once with the new one", async () => {
        auth.refreshToken.mockResolvedValue("token-2");
        respond(status(401), json({packets: [], nextCursor: null}));

        expect(await getPackets()).toEqual({success: true, page: {packets: [], nextCursor: null}});
        expect(authorizationOf(0)).toBe("Bearer token-1");
        expect(authorizationOf(1)).toBe("Bearer token-2");
        expect(auth.signOut).not.toHaveBeenCalled();
    });

    it("signs out when a 401 can't be refreshed", async () => {
        respond(status(401));

        expect(await getPackets()).toEqual({
            success: false, error: "Your session has expired. Please sign in again.", signedOut: true,
        });
        expect(fetchMock).toHaveBeenCalledTimes(1);
        expect(auth.signOut).toHaveBeenCalledTimes(1);
    });

    it("signs out when the retry is refused too, without a second retry", async () => {
        auth.refreshToken.mockResolvedValue("token-2");
        respond(status(401), status(401));

        expect(await deletePacket("p1", true)).toMatchObject({success: false, signedOut: true});
        expect(fetchMock).toHaveBeenCalledTimes(2);
        expect(auth.signOut).toHaveBeenCalledTimes(1);
    });

    it("sends a development token as Dev and signs out on its 401 without trying a refresh", async () => {
        auth.getToken.mockResolvedValue("dev-user:alice");
        respond(status(401));

        expect(await sendPacket(NOTE, true)).toEqual({
            success: false,
            error: "The API refused the development user. Is it running in Development?",
            signedOut: true,
        });
        expect(authorizationOf(0)).toBe("Dev alice");
        expect(auth.refreshToken).not.toHaveBeenCalled();
        expect(auth.signOut).toHaveBeenCalledTimes(1);
    });

    it("may show a sign-in window only for user actions", async () => {
        respond(status(401), status(401), status(401));

        await sendPacket(NOTE, true);
        await deletePacket("p1", true);
        await getPackets();

        expect(auth.getToken.mock.calls).toEqual([[true], [true], [false]]);
        expect(auth.refreshToken.mock.calls).toEqual([[true], [true], [false]]);
    });

    it("doesn't refresh or sign out on a 403", async () => {
        respond(status(403));

        expect(await getPackets()).toEqual({success: false, error: "Request failed (403)."});
        expect(auth.refreshToken).not.toHaveBeenCalled();
        expect(auth.signOut).not.toHaveBeenCalled();
    });

    it("answers with the offline message when the request can't be made", async () => {
        fetchMock.mockRejectedValue(new TypeError("Failed to fetch"));
        expect(await sendPacket(NOTE, true)).toEqual({success: false, error: OFFLINE_MESSAGE});
    });
});

describe("problem details", () => {
    it.each([
        ["title and detail, joined with a colon", {title: "Packet too large", detail: "Notes hold up to 64 KB."},
            "Packet too large: Notes hold up to 64 KB."],
        ["a title that ends a sentence, joined with a space", {title: "Too many requests.", detail: "Try again in a minute."},
            "Too many requests. Try again in a minute."],
        ["the validation errors when there's no detail", {
            title: "One or more validation errors occurred.",
            errors: {body: ["The body is required."], url: ["The URL is too long.", "The URL must be absolute."]},
        }, "One or more validation errors occurred. The body is required. The URL is too long. The URL must be absolute."],
        ["the detail over the validation errors", {title: "Invalid", detail: "Bad body.", errors: {body: ["ignored"]}},
            "Invalid: Bad body."],
        ["a title alone, trimmed", {title: "  Quota exceeded  "}, "Quota exceeded"],
        ["a detail alone", {detail: "You have too many packets."}, "You have too many packets."],
        ["an empty problem, as the status", {}, "Request failed (400)."],
    ])("reads %s", async (_, problem, message) => {
        respond(json(problem, 400, "application/problem+json"));
        expect(await sendPacket(NOTE, true)).toEqual({success: false, error: message});
    });

    it("falls back to the status for a body that isn't JSON", async () => {
        respond(new Response("<h1>Bad gateway</h1>", {status: 502, headers: {"Content-Type": "text/html"}}));
        expect(await sendPacket(NOTE, true)).toEqual({success: false, error: "Request failed (502)."});
    });

    it("falls back to the status for JSON that can't be parsed", async () => {
        respond(new Response("{not json", {status: 500, headers: {"Content-Type": "application/json"}}));
        expect(await getPackets()).toEqual({success: false, error: "Request failed (500)."});
    });
});

describe("the packet endpoints", () => {
    it("posts the packet as JSON to the packets endpoint, keeping its query", async () => {
        respond(json({$type: "Text", id: "p1", title: null, body: "Milk"}, 201));
        await sendPacket(NOTE, true);

        const [url, init] = fetchMock.mock.calls[0];
        expect(url).toBe(`${PACKETS}?api-version=1`);
        expect(init?.method).toBe("POST");
        expect(new Headers(init?.headers).get("Content-Type")).toBe("application/json");
        expect(JSON.parse(init?.body as string)).toEqual(NOTE);
    });

    it("counts a send as done when the echoed packet can't be read", async () => {
        respond(new Response("", {status: 201}));
        expect(await sendPacket(NOTE, true)).toEqual({success: true, packet: undefined});
    });

    it("asks for a page by its cursor and parses it, skipping packets it doesn't know", async () => {
        respond(json({
            packets: [{$type: "Link", id: "p2", name: "Passel", url: "https://passel.link"}, {$type: "Hologram", id: "p3"}],
            nextCursor: "c2",
        }));

        expect(await getPackets("c1")).toEqual({
            success: true,
            page: {
                packets: [{id: "p2", type: "link", title: "Passel", payload: "https://passel.link", isImage: false}],
                nextCursor: "c2",
            },
        });
        expect(urlOf(0).searchParams.get("cursor")).toBe("c1");
        expect(urlOf(0).searchParams.get("api-version")).toBe("1");
    });

    it("reads a missing cursor as the last page", async () => {
        respond(json({packets: [], nextCursor: 12}));
        expect(await getPackets()).toEqual({success: true, page: {packets: [], nextCursor: null}});
    });

    it("deletes by the encoded id, counting a packet that's already gone as deleted", async () => {
        respond(status(404), status(204), json({title: "Server error"}, 500));

        expect(await deletePacket("a/b", true)).toEqual({success: true});
        expect(urlOf(0).pathname).toBe("/api/packets/a%2Fb");
        expect(fetchMock.mock.calls[0][1]?.method).toBe("DELETE");
        expect(await deletePacket("p1", true)).toEqual({success: true});
        expect(await deletePacket("p1", true)).toEqual({success: false, error: "Server error"});
    });
});

describe("the event stream", () => {
    /** A response whose body is the given chunks, then (unless `open`) the end of the stream. */
    function eventStream(chunks: string[], {open = false} = {}): Response {
        const body = new ReadableStream<Uint8Array>({
            start(controller) {
                for (const chunk of chunks) controller.enqueue(new TextEncoder().encode(chunk));
                if (!open) controller.close();
            },
        });
        return new Response(body, {headers: {"Content-Type": "text/event-stream; charset=utf-8"}});
    }

    it("passes on the events it understands until the stream ends", async () => {
        respond(eventStream([
            "event: resync\ndata: {}\n\n",
            ": heartbeat\n\nevent: packet-created\ndata: {\"$type\":\"Text\",\"id\":\"p1\",",
            "\"title\":null,\"body\":\"Milk\"}\n\nevent: packet-updated\ndata: {\"id\":\"p1\"}\n\n",
            "event: packet-deleted\ndata: {\"id\":\"p0\"}\n\n",
        ]));
        const events: LiveEvent[] = [];

        await watchEvents(new AbortController().signal, event => events.push(event));

        expect(events).toEqual([
            {kind: "resync"},
            {kind: "created", packet: {id: "p1", type: "text", title: null, payload: "Milk", isImage: false}},
            {kind: "deleted", id: "p0"},
        ]);
        expect(urlOf(0).pathname).toBe("/api/packets/events");
        expect(new Headers(fetchMock.mock.calls[0][1]?.headers).get("Accept")).toBe("text/event-stream");
        // Live updates never show a sign-in window.
        expect(auth.getToken).toHaveBeenCalledWith(false);
    });

    it("gives up on a stream that stays silent past the heartbeat timeout", async () => {
        vi.useFakeTimers();
        respond(eventStream(["event: resync\ndata: {}\n\n"], {open: true}));
        const events: LiveEvent[] = [];

        const watching = watchEvents(new AbortController().signal, event => events.push(event));
        const failed = expect(watching).rejects.toThrow("The event stream went silent");
        await vi.advanceTimersByTimeAsync(44_999);
        expect(events).toEqual([{kind: "resync"}]);

        await vi.advanceTimersByTimeAsync(1);
        await failed;
    });

    it("refuses a response that isn't an event stream", async () => {
        respond(json({packets: []}));
        await expect(watchEvents(new AbortController().signal, () => {
        })).rejects.toThrow("Expected an event stream but got \"application/json\"");
    });

    it("fails with the problem when the API refuses the stream", async () => {
        respond(json({title: "Too many streams"}, 429));
        await expect(watchEvents(new AbortController().signal, () => {
        })).rejects.toThrow("Too many streams");
    });

    it("fails as signed out when the session can't be used", async () => {
        respond(status(401));
        await expect(watchEvents(new AbortController().signal, () => {
        })).rejects.toBeInstanceOf(SignedOutError);
    });

    it("passes the abort signal to the request", async () => {
        respond(eventStream([]));
        const controller = new AbortController();

        await watchEvents(controller.signal, () => {
        });

        expect(fetchMock.mock.calls[0][1]?.signal).toBe(controller.signal);
    });
});
