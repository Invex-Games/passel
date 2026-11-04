import {SignedOutError} from "./api.js";
import type {LiveEvent} from "./types.js";

// Keeping the live event stream open: connect, forward its events, and when it ends or fails, reconnect with
// jittered backoff until stopped. The stream counts as live once the server's first resync arrives. A stream
// refused for lack of a session is not retried: reconnecting with the same session would fail again.

/** The longest wait between two reconnects. */
export const MAX_RETRY_DELAY_MS = 60_000;

/**
 * Exponential backoff from 1 second up to a minute, with full jitter (anywhere from zero to the backoff), so
 * clients that all lost the stream at the same moment, as after a server restart, don't all reconnect at once.
 */
export function retryDelay(failures: number, random: () => number = Math.random): number {
    const base = Math.min(1000 * 2 ** Math.min(failures, 6), MAX_RETRY_DELAY_MS);
    return random() * base;
}

export interface Timers {
    setTimeout(callback: () => void, ms: number): unknown;

    clearTimeout(handle: unknown): void;
}

export interface LiveConnectionOptions {
    /** Opens the stream and resolves when it ends; PacketsApi.watchEvents. */
    watch(signal: AbortSignal, onEvent: (event: LiveEvent) => void): Promise<void>;

    onEvent(event: LiveEvent): void;

    /** The stream became live (its first resync arrived) or stopped being live. */
    onStatus?(live: boolean): void;

    /** The stream was refused for lack of a session; the connection has stopped and waits for start(). */
    onSignedOut?(error: SignedOutError): void;

    /** The stream ended or failed and a reconnect is scheduled. Defaults to a console warning for failures. */
    onDisconnected?(error: unknown): void;

    timers?: Timers;
    random?: () => number;
}

const defaultTimers: Timers = {
    setTimeout: (callback, ms) => setTimeout(callback, ms),
    clearTimeout: handle => clearTimeout(handle as ReturnType<typeof setTimeout>),
};

export class LiveConnection {
    private stream: AbortController | null = null;
    private retryTimer: unknown = undefined;
    private failures = 0;
    private running = false;
    private isLive = false;

    constructor(private readonly options: LiveConnectionOptions) {
    }

    /** Whether the stream is open and has had its first resync. */
    get live(): boolean {
        return this.isLive;
    }

    /** Whether the connection is wanted: started and not stopped since. */
    get started(): boolean {
        return this.running;
    }

    /** Connects, unless already started. */
    start(): void {
        if (this.running) return;
        this.running = true;
        this.failures = 0;
        void this.connect();
    }

    /** Closes the stream and cancels any reconnect. */
    stop(): void {
        this.running = false;
        this.timers.clearTimeout(this.retryTimer);
        this.retryTimer = undefined;
        this.stream?.abort();
        this.stream = null;
        this.setLive(false);
    }

    private get timers(): Timers {
        return this.options.timers ?? defaultTimers;
    }

    private async connect(): Promise<void> {
        this.retryTimer = undefined;
        const controller = new AbortController();
        this.stream = controller;
        let failure: unknown = null;

        try {
            await this.options.watch(controller.signal, event => {
                if (this.stream !== controller) return;
                if (event.kind === "resync") {
                    this.failures = 0;
                    this.setLive(true);
                }
                this.options.onEvent(event);
            });
        } catch (err) {
            if (controller.signal.aborted) return;

            if (err instanceof SignedOutError) {
                if (this.stream === controller) {
                    this.stop();
                    this.options.onSignedOut?.(err);
                }
                return;
            }

            failure = err;
        }

        if (this.stream !== controller) return;
        this.stream = null;
        this.setLive(false);
        if (!this.running) return;

        if (this.options.onDisconnected) this.options.onDisconnected(failure);
        else if (failure) console.warn("Passel: live updates disconnected", failure);

        this.retryTimer = this.timers.setTimeout(() => void this.connect(), retryDelay(this.failures++, this.options.random));
    }

    private setLive(value: boolean): void {
        if (this.isLive === value) return;
        this.isLive = value;
        this.options.onStatus?.(value);
    }
}
