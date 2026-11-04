import {
    errorMessage,
    FeedRequestError,
    FeedStore,
    LiveConnection,
    type FeedCache,
    type FeedState,
    type LiveEvent,
    type PacketsApi,
    type Timers,
} from "@passel/core";
import {SESSION_EXPIRED_MESSAGE, SessionError, type Session} from "./session.js";

// The web app's state, kept apart from the page so it can be unit-tested: who is signed in, the feed (FeedStore from
// @passel/core, with its paging and live-event rules), and the live connection that keeps it current while the tab
// is visible. The views render AppState and call the methods below; they hold no state of their own beyond what's
// on screen.

/**
 * A resync after the stream reconnects waits up to this long before refetching, so tabs that all lost the stream at
 * the same moment (a server restart) don't all hit the API at once.
 */
export const MAX_RESYNC_DELAY_MS = 3000;

export type AuthState =
    | { status: "signedOut"; /** Why the user was signed out without asking, or why signing in failed. */ message: string | null }
    | { status: "signedIn"; account: string };

export interface AppState {
    auth: AuthState;
    feed: FeedState;
    /** Whether the live stream is open, so the feed is current without a refresh. */
    live: boolean;
    refreshing: boolean;
    /** Why the first load failed, while there is nothing (not even an earlier page) to show instead. */
    loadError: string | null;
    /** A failure worth telling the user about that doesn't replace the feed, such as a refresh that failed. */
    notice: string | null;
}

export interface AppControllerOptions {
    session: Session;
    /** The API client; only the feed source and the event stream are used. */
    api: Pick<PacketsApi, "feedSource" | "watchEvents">;
    /** Where the feed is kept between visits; nothing by default (the offline cache is roadmap 2.E1). */
    cache?: FeedCache;
    timers?: Timers;
    random?: () => number;
    now?: () => number;
}

const noCache: FeedCache = {
    load: async () => null,
    save: async () => {
    },
    clear: async () => {
    },
};

const defaultTimers: Timers = {
    setTimeout: (callback, ms) => setTimeout(callback, ms),
    clearTimeout: handle => clearTimeout(handle as ReturnType<typeof setTimeout>),
};

export class AppController {
    readonly feed: FeedStore;

    private current: AppState;
    private readonly listeners = new Set<(state: AppState) => void>();
    private readonly live: LiveConnection;
    private readonly timers: Timers;
    private readonly random: () => number;
    private readonly unsubscribeFeed: () => void;

    private visible = true;
    private resyncTimer: unknown = undefined;

    /** Whether a resync has been fetched since the stream was started: the first is immediate, later ones spread out. */
    private resynced = false;

    constructor(private readonly options: AppControllerOptions) {
        this.timers = options.timers ?? defaultTimers;
        this.random = options.random ?? Math.random;
        this.feed = new FeedStore(options.api.feedSource(), options.cache ?? noCache, options.now);

        this.current = {
            auth: {status: "signedOut", message: null},
            feed: this.feed.state,
            live: false,
            refreshing: false,
            loadError: null,
            notice: null,
        };

        this.unsubscribeFeed = this.feed.subscribe(feed => this.update({feed}));

        this.live = new LiveConnection({
            watch: (signal, onEvent) => options.api.watchEvents(signal, onEvent),
            onEvent: event => this.onLiveEvent(event),
            onStatus: live => this.update({live}),
            onSignedOut: err => this.sessionLost(err.message),
            timers: this.timers,
            random: this.random,
        });
    }

    get state(): AppState {
        return this.current;
    }

    subscribe(listener: (state: AppState) => void): () => void {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    }

    /** Picks up the session the tab already has (a reload), or shows the sign-in page. */
    start(): void {
        const account = this.options.session.account();
        if (account) {
            this.enterSignedIn(account);
        } else {
            this.update({auth: {status: "signedOut", message: this.options.session.hasExpired ? SESSION_EXPIRED_MESSAGE : null}});
        }
    }

    /** Signs in with an ID token from Google Identity Services. */
    async signInWithGoogle(credential: string, nonce: string | null): Promise<void> {
        await this.signIn(() => this.options.session.acceptGoogleCredential(credential, nonce));
    }

    /** Signs in as a development user (DEV_SIGN_IN builds only). */
    async signInAsDevUser(name: string): Promise<void> {
        await this.signIn(() => this.options.session.signInAsDevUser(name));
    }

    async signOut(): Promise<void> {
        this.options.session.signOut();
        this.stopLive();
        this.update({auth: {status: "signedOut", message: null}, loadError: null, notice: null});
        await this.feed.clear();
    }

    /** Fetches the first page again; `manual` says the user asked, so a failure is reported. */
    async refresh(manual = false): Promise<void> {
        if (this.current.auth.status !== "signedIn") return;

        this.update({refreshing: true});
        try {
            await this.feed.refresh();
            this.update({loadError: null, notice: null});
        } catch (err) {
            if (!this.feed.state.loaded) this.update({loadError: errorMessage(err)});
            this.handleFeedError(err, manual ? "Couldn't refresh" : null);
        } finally {
            this.update({refreshing: false});
        }
    }

    /** Loads older items; `retry` retries after a failure, which otherwise stops automatic loading. */
    async loadMore(retry = false): Promise<void> {
        if (this.current.auth.status !== "signedIn") return;

        try {
            await this.feed.loadMore(retry);
        } catch (err) {
            // The failure is shown at the end of the feed, with a retry button, through the feed state
            this.handleFeedError(err, null);
        }
    }

    /** The tab was shown or hidden: the live stream is only kept open while someone can see it. */
    setVisible(visible: boolean): void {
        if (this.visible === visible) return;
        this.visible = visible;

        if (this.current.auth.status !== "signedIn") return;
        if (visible) this.startLive();
        else this.stopLive();
    }

    dismissNotice(): void {
        this.update({notice: null});
    }

    dispose(): void {
        this.stopLive();
        this.unsubscribeFeed();
        this.listeners.clear();
    }

    // ── Private ──────────────────────────────────────────

    private async signIn(accept: () => string): Promise<void> {
        let account: string;
        try {
            account = accept();
        } catch (err) {
            if (!(err instanceof SessionError)) throw err;
            this.update({auth: {status: "signedOut", message: err.message}});
            return;
        }

        // The account may have changed, so the previous feed must not stay on screen
        this.stopLive();
        await this.feed.clear();
        this.enterSignedIn(account);
    }

    private enterSignedIn(account: string): void {
        this.update({auth: {status: "signedIn", account}, loadError: null, notice: null});
        void this.refresh();
        if (this.visible) this.startLive();
    }

    /**
     * The API refused the session, or it ran out: forget it and the feed, and say why on the sign-in page, which
     * offers One Tap to bring the user straight back.
     */
    private sessionLost(message: string): void {
        if (this.current.auth.status !== "signedIn") return;

        this.options.session.drop();
        this.stopLive();
        this.update({
            auth: {status: "signedOut", message: this.options.session.hasExpired ? SESSION_EXPIRED_MESSAGE : message},
            loadError: null,
            notice: null,
        });
        void this.feed.clear();
    }

    private handleFeedError(err: unknown, noticePrefix: string | null): void {
        if (err instanceof FeedRequestError && err.signedOut) {
            this.sessionLost(err.message);
            return;
        }
        if (noticePrefix) this.update({notice: `${noticePrefix}: ${errorMessage(err)}`});
    }

    private startLive(): void {
        if (this.live.started) return;
        this.resynced = false;
        this.live.start();
    }

    private stopLive(): void {
        this.timers.clearTimeout(this.resyncTimer);
        this.resyncTimer = undefined;
        this.live.stop();
    }

    private onLiveEvent(event: LiveEvent): void {
        switch (event.kind) {
            case "resync":
                this.scheduleResync();
                break;
            case "created":
                this.feed.applyCreated(event.packet);
                break;
            case "updated":
                this.feed.applyUpdated(event.packet);
                break;
            case "deleted":
                this.feed.applyDeleted(event.id);
                break;
        }
    }

    /**
     * Catches up on anything that happened while the stream was down. The first resync after the stream starts runs
     * at once, so the feed is fresh when the tab comes back into view; later ones (reconnects) are spread out.
     */
    private scheduleResync(): void {
        this.timers.clearTimeout(this.resyncTimer);
        if (!this.resynced) {
            this.resynced = true;
            this.syncLatest();
            return;
        }
        this.resyncTimer = this.timers.setTimeout(() => this.syncLatest(), this.random() * MAX_RESYNC_DELAY_MS);
    }

    private syncLatest(): void {
        this.feed.syncLatest().catch(err => this.handleFeedError(err, null));
    }

    private update(patch: Partial<AppState>): void {
        this.current = {...this.current, ...patch};
        for (const listener of this.listeners) listener(this.current);
    }
}
