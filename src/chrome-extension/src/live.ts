import {SignedOutError, watchEvents} from "./api.js";
import type {FeedPortMessage} from "./types.js";

// Only the service worker uses this module. While at least one popup holds a feed port, the API's event
// stream is kept open and its events are forwarded to every port; it's closed once the last port goes.
// The popup pings over its port to keep the service worker alive while it's open.

const MAX_RETRY_DELAY_MS = 60_000;

const ports = new Set<chrome.runtime.Port>();
let stream: AbortController | null = null;
let retryTimer: ReturnType<typeof setTimeout> | undefined;
let failures = 0;
let live = false;

export function addFeedPort(port: chrome.runtime.Port): void {
    ports.add(port);
    port.onDisconnect.addListener(() => {
        ports.delete(port);
        if (ports.size === 0) stop();
    });
    // Keep-alive pings need no handling: receiving them is what keeps the service worker running.
    port.onMessage.addListener(() => {
    });

    post(port, {type: "status", live});
    if (!stream && retryTimer === undefined) void connect();
}

async function connect(): Promise<void> {
    retryTimer = undefined;
    const controller = new AbortController();
    stream = controller;

    try {
        await watchEvents(controller.signal, event => {
            if (event.kind === "resync") {
                failures = 0;
                setLive(true);
            }
            broadcast({type: "event", event});
        });
    } catch (err) {
        if (controller.signal.aborted) return;

        if (err instanceof SignedOutError) {
            // Reconnecting with the same session would fail again; the popup reconnects once signed in.
            stream = null;
            setLive(false);
            broadcast({type: "signedOut"});
            return;
        }

        console.warn("Passel: live updates disconnected", err);
    }

    if (stream !== controller) return;
    stream = null;
    setLive(false);
    if (ports.size > 0) retryTimer = setTimeout(() => void connect(), retryDelay(failures++));
}

function stop(): void {
    clearTimeout(retryTimer);
    retryTimer = undefined;
    stream?.abort();
    stream = null;
    failures = 0;
    setLive(false);
}

/**
 * Exponential backoff from 1 second up to a minute, with full jitter (anywhere from zero to the backoff), so
 * clients that all lost the stream at the same moment, as after a server restart, don't all reconnect at once.
 */
function retryDelay(failures: number): number {
    const base = Math.min(1000 * 2 ** Math.min(failures, 6), MAX_RETRY_DELAY_MS);
    return Math.random() * base;
}

function setLive(value: boolean): void {
    if (live === value) return;
    live = value;
    broadcast({type: "status", live});
}

function broadcast(message: FeedPortMessage): void {
    for (const port of ports) post(port, message);
}

function post(port: chrome.runtime.Port, message: FeedPortMessage): void {
    try {
        port.postMessage(message);
    } catch {
        // The popup closed; its disconnect listener removes the port.
    }
}
