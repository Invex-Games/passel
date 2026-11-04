import {afterEach, beforeEach, describe, expect, it, vi} from "vitest";
import type {FeedPortMessage, LiveEvent} from "@passel/core";
import {asChrome, fakePort, flushPromises, installChrome, type FakePort} from "./chrome.js";

/** One call of watchEvents: the test ends it, fails it or sends events through it. */
interface Stream {
    signal: AbortSignal;
    send(event: LiveEvent): void;
    end(): void;
    fail(err: unknown): void;
}

const api = vi.hoisted(() => {
    class SignedOutError extends Error {
    }

    return {SignedOutError, watchEvents: vi.fn()};
});
vi.mock("../src/api.js", () => api);

let streams: Stream[];
let addFeedPort: (port: chrome.runtime.Port) => void;
let restartLive: () => void;

beforeEach(async () => {
    installChrome();
    vi.useFakeTimers();
    vi.spyOn(console, "warn").mockImplementation(() => {
    });
    // The top of the jitter, so each retry waits its whole backoff.
    vi.spyOn(Math, "random").mockReturnValue(1);

    streams = [];
    api.watchEvents.mockImplementation((signal: AbortSignal, onEvent: (event: LiveEvent) => void) =>
        new Promise<void>((resolve, reject) => {
            streams.push({signal, send: onEvent, end: resolve, fail: reject});
        }));

    // The module keeps its ports and stream between calls, so each test starts from a fresh copy.
    vi.resetModules();
    ({addFeedPort, restartLive} = await import("../src/live.js"));
});

afterEach(() => {
    vi.useRealTimers();
});

function connect(): FakePort {
    const port = fakePort();
    addFeedPort(asChrome<chrome.runtime.Port>(port));
    return port;
}

function messages(port: FakePort): FeedPortMessage[] {
    return port.postMessage.mock.calls.map(([message]) => message as FeedPortMessage);
}

/** Fails the open stream and lets live.ts schedule its retry. */
async function failStream(): Promise<void> {
    streams.at(-1)!.fail(new Error("network down"));
    await flushPromises();
}

/** How long after the last failure the next stream opens. */
async function nextRetryDelay(): Promise<number> {
    const before = streams.length;
    let waited = 0;
    while (streams.length === before) {
        await vi.advanceTimersByTimeAsync(250);
        waited += 250;
        if (waited > 120_000) throw new Error("no retry");
    }
    return waited;
}

describe("ports", () => {
    it("opens one stream for every popup and tells each one it isn't live yet", () => {
        const first = connect();
        const second = connect();

        expect(api.watchEvents).toHaveBeenCalledTimes(1);
        expect(messages(first)).toEqual([{type: "status", live: false}]);
        expect(messages(second)).toEqual([{type: "status", live: false}]);
    });

    it("goes live on the first resync and forwards every event to every port", () => {
        const first = connect();
        const second = connect();
        const packet = {id: "p1", type: "text" as const, title: null, payload: "Milk", isImage: false};

        streams[0].send({kind: "resync"});
        streams[0].send({kind: "created", packet});

        for (const port of [first, second]) {
            expect(messages(port).slice(1)).toEqual([
                {type: "status", live: true},
                {type: "event", event: {kind: "resync"}},
                {type: "event", event: {kind: "created", packet}},
            ]);
        }
    });

    it("tells a popup that connects later that the stream is live", () => {
        connect();
        streams[0].send({kind: "resync"});

        expect(messages(connect())).toEqual([{type: "status", live: true}]);
    });

    it("closes the stream when the last port goes, not before", async () => {
        const first = connect();
        const second = connect();

        first.disconnect();
        expect(streams[0].signal.aborted).toBe(false);

        second.disconnect();
        expect(streams[0].signal.aborted).toBe(true);

        // The abort ends the stream with an error; that's no reason to reconnect.
        streams[0].fail(new DOMException("aborted", "AbortError"));
        await vi.advanceTimersByTimeAsync(120_000);
        expect(api.watchEvents).toHaveBeenCalledTimes(1);
    });

    it("ignores a port that has already closed", () => {
        const port = connect();
        port.postMessage.mockImplementation(() => {
            throw new Error("Attempting to use a disconnected port object");
        });

        expect(() => streams[0].send({kind: "resync"})).not.toThrow();
    });
});

describe("reconnecting", () => {
    it("backs off exponentially from one second, up to a minute", async () => {
        connect();

        const delays: number[] = [];
        for (let i = 0; i < 8; i++) {
            await failStream();
            delays.push(await nextRetryDelay());
        }

        expect(delays).toEqual([1000, 2000, 4000, 8000, 16_000, 32_000, 60_000, 60_000]);
    });

    it("waits anywhere from nothing up to the backoff (full jitter)", async () => {
        vi.mocked(Math.random).mockReturnValue(0.25);
        connect();

        await failStream();
        expect(await nextRetryDelay()).toBe(250);
    });

    it("reconnects when the stream ends, too", async () => {
        const port = connect();
        streams[0].send({kind: "resync"});

        streams[0].end();
        await flushPromises();

        expect(messages(port).at(-1)).toEqual({type: "status", live: false});
        expect(await nextRetryDelay()).toBe(1000);
    });

    it("starts the backoff again once a stream has resynced", async () => {
        connect();
        await failStream();
        await nextRetryDelay();
        await failStream();
        expect(await nextRetryDelay()).toBe(2000);

        streams.at(-1)!.send({kind: "resync"});
        await failStream();
        expect(await nextRetryDelay()).toBe(1000);
    });

    it("doesn't retry once the last port has gone", async () => {
        const port = connect();
        await failStream();

        port.disconnect();
        await vi.advanceTimersByTimeAsync(120_000);

        expect(api.watchEvents).toHaveBeenCalledTimes(1);
    });

    it("doesn't open a second stream for a port that connects while a retry is waiting", async () => {
        connect();
        await failStream();

        connect();
        expect(api.watchEvents).toHaveBeenCalledTimes(1);
        expect(await nextRetryDelay()).toBe(1000);
    });

    it("stops and tells every popup when the session can't be used, until a popup connects again", async () => {
        const port = connect();
        streams[0].send({kind: "resync"});

        streams[0].fail(new api.SignedOutError("Your session has expired. Please sign in again."));
        await flushPromises();

        expect(messages(port).slice(-2)).toEqual([{type: "status", live: false}, {type: "signedOut"}]);
        await vi.advanceTimersByTimeAsync(120_000);
        expect(api.watchEvents).toHaveBeenCalledTimes(1);

        // Signed in again, the popup reconnects its port.
        connect();
        expect(api.watchEvents).toHaveBeenCalledTimes(2);
    });
});

describe("a change of session", () => {
    it("ends the stream opened with the old session and opens one for every page still holding a port", async () => {
        // The side panel and a popup beside it; the popup signs out and signs in as someone else.
        const panel = connect();
        const popup = connect();
        streams[0].send({kind: "resync"});

        popup.disconnect();
        restartLive();

        expect(streams[0].signal.aborted).toBe(true);
        expect(api.watchEvents).toHaveBeenCalledTimes(2);
        expect(messages(panel).at(-1)).toEqual({type: "status", live: false});

        // The old stream ending isn't a failure to retry.
        streams[0].fail(new DOMException("aborted", "AbortError"));
        await vi.advanceTimersByTimeAsync(120_000);
        expect(api.watchEvents).toHaveBeenCalledTimes(2);

        // Events reach the panel from the new stream only.
        const packet = {id: "p2", type: "text" as const, title: null, payload: "Eggs", isImage: false};
        streams[1].send({kind: "created", packet});
        expect(messages(panel).at(-1)).toEqual({type: "event", event: {kind: "created", packet}});
    });

    it("tells every page when the new session is a signed-out one", async () => {
        const panel = connect();

        restartLive();
        streams[1].fail(new api.SignedOutError("You're not signed in."));
        await flushPromises();

        expect(messages(panel).at(-1)).toEqual({type: "signedOut"});
        await vi.advanceTimersByTimeAsync(120_000);
        expect(api.watchEvents).toHaveBeenCalledTimes(2);
    });

    it("opens no stream while no page holds a port, and drops a waiting retry", async () => {
        const port = connect();
        await failStream();

        restartLive();
        expect(api.watchEvents).toHaveBeenCalledTimes(2);
        // The retry that was waiting doesn't open a third.
        await vi.advanceTimersByTimeAsync(120_000);
        expect(api.watchEvents).toHaveBeenCalledTimes(2);

        port.disconnect();
        restartLive();
        expect(api.watchEvents).toHaveBeenCalledTimes(2);
    });
});
