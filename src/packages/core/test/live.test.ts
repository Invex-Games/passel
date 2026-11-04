import {afterEach, beforeEach, describe, expect, it, vi} from "vitest";
import {SignedOutError} from "../src/api.js";
import {LiveConnection, MAX_RETRY_DELAY_MS, retryDelay} from "../src/live.js";
import type {LiveEvent} from "../src/types.js";

/** One stream opened by the connection, which the test drives. */
interface Stream {
    signal: AbortSignal;
    emit(event: LiveEvent): void;
    end(): void;
    fail(error: unknown): void;
}

let streams: Stream[];
let events: LiveEvent[];
let statuses: boolean[];
let signedOut: SignedOutError[];
let connection: LiveConnection;

beforeEach(() => {
    vi.useFakeTimers();
    streams = [];
    events = [];
    statuses = [];
    signedOut = [];
    connection = new LiveConnection({
        watch: (signal, onEvent) => new Promise<void>((resolve, reject) => {
            signal.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
            streams.push({signal, emit: onEvent, end: resolve, fail: reject});
        }),
        onEvent: event => events.push(event),
        onStatus: live => statuses.push(live),
        onSignedOut: error => signedOut.push(error),
        onDisconnected: () => {
        },
        // The longest wait every time, so the tests know when the reconnect is due
        random: () => 1,
    });
});

afterEach(() => {
    connection.stop();
    vi.useRealTimers();
});

describe("retryDelay", () => {
    it("doubles from a second up to a minute, scaled by the jitter", () => {
        expect([0, 1, 2, 5, 6, 20].map(n => retryDelay(n, () => 1))).toEqual([1000, 2000, 4000, 32_000, MAX_RETRY_DELAY_MS, MAX_RETRY_DELAY_MS]);
        expect(retryDelay(3, () => 0.5)).toBe(4000);
        expect(retryDelay(3, () => 0)).toBe(0);
    });
});

describe("LiveConnection", () => {
    it("forwards events, and is live from the first resync", async () => {
        connection.start();
        connection.start();
        expect(streams).toHaveLength(1);
        expect(connection.live).toBe(false);

        streams[0].emit({kind: "resync"});
        streams[0].emit({kind: "deleted", id: "a"});

        expect(connection.live).toBe(true);
        expect(statuses).toEqual([true]);
        expect(events).toEqual([{kind: "resync"}, {kind: "deleted", id: "a"}]);
    });

    it("reconnects with growing delays after failures, and starts over after a resync", async () => {
        connection.start();

        streams[0].fail(new Error("dropped"));
        await vi.advanceTimersByTimeAsync(999);
        expect(streams).toHaveLength(1);
        await vi.advanceTimersByTimeAsync(1);
        expect(streams).toHaveLength(2);

        streams[1].fail(new Error("dropped"));
        await vi.advanceTimersByTimeAsync(1999);
        expect(streams).toHaveLength(2);
        await vi.advanceTimersByTimeAsync(1);
        expect(streams).toHaveLength(3);

        // A stream that got as far as its resync was healthy, so the next drop waits the shortest time again
        streams[2].emit({kind: "resync"});
        streams[2].end();
        await vi.advanceTimersByTimeAsync(1000);
        expect(streams).toHaveLength(4);
        expect(statuses).toEqual([true, false]);
    });

    it("stops for good when the stream is refused for lack of a session", async () => {
        connection.start();

        streams[0].fail(new SignedOutError("Your session has expired."));
        await vi.advanceTimersByTimeAsync(MAX_RETRY_DELAY_MS);

        expect(streams).toHaveLength(1);
        expect(signedOut.map(e => e.message)).toEqual(["Your session has expired."]);
        expect(connection.started).toBe(false);

        connection.start();
        expect(streams).toHaveLength(2);
    });

    it("stop closes the stream and cancels the reconnect", async () => {
        connection.start();
        streams[0].emit({kind: "resync"});

        connection.stop();
        expect(streams[0].signal.aborted).toBe(true);
        expect(connection.live).toBe(false);

        await vi.advanceTimersByTimeAsync(MAX_RETRY_DELAY_MS);
        expect(streams).toHaveLength(1);

        connection.start();
        streams[1].fail(new Error("dropped"));
        await Promise.resolve();
        connection.stop();
        await vi.advanceTimersByTimeAsync(MAX_RETRY_DELAY_MS);
        expect(streams).toHaveLength(2);
    });

    it("ignores events from a stream it has replaced", async () => {
        connection.start();
        const old = streams[0];
        connection.stop();
        connection.start();

        old.emit({kind: "deleted", id: "stale"});

        expect(events).toEqual([]);
    });
});
