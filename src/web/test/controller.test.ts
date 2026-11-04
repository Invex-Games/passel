import {FeedRequestError, SignedOutError, type LiveEvent, type Packet, type PacketPage} from "@passel/core";
import {afterEach, beforeEach, describe, expect, it, vi} from "vitest";
import {AppController, HIDDEN_GRACE_MS, MAX_RESYNC_DELAY_MS} from "../src/app/controller.js";
import {SESSION_EXPIRED_MESSAGE, Session} from "../src/app/session.js";
import {CLIENT_ID, googleToken, MemoryStore} from "./fakes.js";

const text = (id: string): Packet => ({id, type: "text", title: null, payload: `body ${id}`, isImage: false});
const page = (ids: string[], nextCursor: string | null = null): PacketPage => ({packets: ids.map(text), nextCursor});

/** One event stream the controller opened, driven by the test. */
interface Stream {
    signal: AbortSignal;
    emit(event: LiveEvent): void;
    fail(error: unknown): void;
    ended: boolean;
}

/** The API as the controller sees it: pages served by `onFetch`, streams recorded in `streams`. */
class FakeApi {
    onFetch: (cursor?: string) => Promise<PacketPage> = async () => page([]);
    readonly cursors: (string | undefined)[] = [];
    readonly streams: Stream[] = [];

    get openStreams(): Stream[] {
        return this.streams.filter(s => !s.signal.aborted && !s.ended);
    }

    feedSource() {
        return {
            fetchPage: (cursor?: string) => {
                this.cursors.push(cursor);
                return this.onFetch(cursor);
            },
        };
    }

    watchEvents(signal: AbortSignal, onEvent: (event: LiveEvent) => void): Promise<void> {
        return new Promise((_, reject) => {
            signal.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
            const stream: Stream = {
                signal,
                emit: onEvent,
                fail: error => {
                    stream.ended = true;
                    reject(error);
                },
                ended: false,
            };
            this.streams.push(stream);
        });
    }
}

let now: number;
let tab: MemoryStore;
let browser: MemoryStore;
let session: Session;
let api: FakeApi;
let controller: AppController;

const ids = () => controller.state.feed.packets.map(p => p.id);

function create(): AppController {
    controller = new AppController({session, api, random: () => 1, now: () => now});
    return controller;
}

beforeEach(() => {
    vi.useFakeTimers();
    now = Date.now();
    tab = new MemoryStore();
    browser = new MemoryStore();
    session = new Session({clientId: CLIENT_ID, devSignIn: true, tabStore: tab, browserStore: browser, now: () => now});
    api = new FakeApi();
    create();
});

afterEach(() => {
    controller.dispose();
    vi.useRealTimers();
});

describe("signing in", () => {
    it("starts on the sign-in page without a session, and fetches nothing", () => {
        controller.start();

        expect(controller.state.auth).toEqual({status: "signedOut", message: null});
        expect(api.cursors).toEqual([]);
        expect(api.streams).toEqual([]);
    });

    it("picks up the tab's session on a reload, loads the feed and opens the stream", async () => {
        session.acceptGoogleCredential(googleToken({}, now), null);
        api.onFetch = async () => page(["a", "b"], "c1");

        controller.start();
        await vi.runOnlyPendingTimersAsync();

        expect(controller.state.auth).toEqual({status: "signedIn", account: "alice@example.com"});
        expect(ids()).toEqual(["a", "b"]);
        expect(api.openStreams).toHaveLength(1);
    });

    it("signs in as a development user, dropping whatever feed was shown before", async () => {
        session.signInAsDevUser("alice");
        api.onFetch = async () => page(["alices"]);
        controller.start();
        await vi.runOnlyPendingTimersAsync();

        api.onFetch = async () => page(["bobs"]);
        const signingIn = controller.signInAsDevUser("bob");
        expect(ids()).toEqual([]);
        await signingIn;
        await vi.runOnlyPendingTimersAsync();

        expect(controller.state.auth).toEqual({status: "signedIn", account: "bob (dev)"});
        expect(ids()).toEqual(["bobs"]);
        expect(api.openStreams).toHaveLength(1);
    });

    it("says why a sign-in was refused and stays signed out", async () => {
        await controller.signInWithGoogle(googleToken({aud: "another-app"}, now), null);

        expect(controller.state.auth).toEqual({status: "signedOut", message: "Google sent a sign-in meant for another app."});
        expect(api.cursors).toEqual([]);
    });

    it("signing out forgets the feed and closes the stream", async () => {
        session.signInAsDevUser("alice");
        api.onFetch = async () => page(["a"]);
        controller.start();
        await vi.runOnlyPendingTimersAsync();

        await controller.signOut();

        expect(controller.state.auth).toEqual({status: "signedOut", message: null});
        expect(ids()).toEqual([]);
        expect(api.openStreams).toEqual([]);
        expect(session.token()).toBeNull();
    });
});

describe("live updates", () => {
    beforeEach(async () => {
        session.signInAsDevUser("alice");
        api.onFetch = async () => page(["a", "b"]);
        controller.start();
        await vi.runOnlyPendingTimersAsync();
    });

    it("is live from the first resync, which catches up at once", async () => {
        api.onFetch = async () => page(["new", "a", "b"]);

        api.streams[0].emit({kind: "resync"});
        await vi.advanceTimersByTimeAsync(0);

        expect(controller.state.live).toBe(true);
        expect(api.cursors).toHaveLength(2);
        expect(ids()).toEqual(["new", "a", "b"]);
    });

    it("applies created, updated and deleted events to the feed", () => {
        const withPreview: Packet = {...text("a"), type: "link", payload: "https://example.com", preview: {title: "Example", description: null, imageUrl: null}};

        api.streams[0].emit({kind: "created", packet: text("live")});
        api.streams[0].emit({kind: "updated", packet: withPreview});
        api.streams[0].emit({kind: "deleted", id: "b"});

        expect(ids()).toEqual(["live", "a"]);
        expect(controller.state.feed.packets[1]).toEqual(withPreview);
    });

    it("spreads the resync after a reconnect over a few seconds", async () => {
        api.streams[0].emit({kind: "resync"});
        await vi.advanceTimersByTimeAsync(0);
        const fetches = api.cursors.length;

        // The stream drops and comes back after its backoff; its resync waits up to MAX_RESYNC_DELAY_MS
        api.streams[0].fail(new Error("dropped"));
        await vi.advanceTimersByTimeAsync(1000);
        expect(api.openStreams).toHaveLength(1);
        expect(controller.state.live).toBe(false);

        api.streams[1].emit({kind: "resync"});
        await vi.advanceTimersByTimeAsync(MAX_RESYNC_DELAY_MS - 1);
        expect(api.cursors).toHaveLength(fetches);
        await vi.advanceTimersByTimeAsync(1);
        expect(api.cursors).toHaveLength(fetches + 1);
    });

    it("keeps the stream of a tab hidden for a moment, so glancing away opens no new one", async () => {
        api.streams[0].emit({kind: "resync"});
        await vi.advanceTimersByTimeAsync(0);
        const fetches = api.cursors.length;

        for (let i = 0; i < 20; i++) {
            controller.setVisible(false);
            await vi.advanceTimersByTimeAsync(HIDDEN_GRACE_MS - 1);
            controller.setVisible(true);
        }

        expect(api.streams).toHaveLength(1);
        expect(api.openStreams).toHaveLength(1);
        expect(controller.state.live).toBe(true);
        expect(api.cursors).toHaveLength(fetches);
    });

    it("closes the stream once the tab has been hidden a while, and catches up at once when it's shown", async () => {
        api.streams[0].emit({kind: "resync"});
        await vi.advanceTimersByTimeAsync(0);

        controller.setVisible(false);
        expect(api.openStreams).toHaveLength(1);
        await vi.advanceTimersByTimeAsync(HIDDEN_GRACE_MS);
        expect(api.openStreams).toEqual([]);
        expect(controller.state.live).toBe(false);

        api.onFetch = async () => page(["missed", "a", "b"]);
        controller.setVisible(true);
        expect(api.openStreams).toHaveLength(1);
        api.openStreams[0].emit({kind: "resync"});
        await vi.advanceTimersByTimeAsync(0);

        expect(ids()).toEqual(["missed", "a", "b"]);
    });

    it("signs out when the stream is refused for lack of a session", async () => {
        api.streams[0].fail(new SignedOutError("The API refused the development user. Is it running in Development?"));
        await vi.advanceTimersByTimeAsync(0);

        expect(controller.state.auth).toEqual({
            status: "signedOut",
            message: "The API refused the development user. Is it running in Development?",
        });
        expect(ids()).toEqual([]);
        expect(session.token()).toBeNull();
    });
});

describe("failures", () => {
    it("shows why the first load failed, and loads on retry", async () => {
        session.signInAsDevUser("alice");
        api.onFetch = async () => {
            throw new FeedRequestError("Couldn't reach Passel.");
        };
        controller.start();
        await vi.runOnlyPendingTimersAsync();

        expect(controller.state.loadError).toBe("Couldn't reach Passel.");

        api.onFetch = async () => page(["a"]);
        await controller.refresh(true);

        expect(controller.state.loadError).toBeNull();
        expect(ids()).toEqual(["a"]);
    });

    it("shows a first load that fails as failed when the stream's first resync overtook the sign-in's refresh", async () => {
        session.signInAsDevUser("alice");
        let answerRefresh!: (page: PacketPage) => void;
        api.onFetch = () => new Promise(resolve => answerRefresh = resolve);
        controller.start();

        // The resync's fetch replaces the refresh's, so the refresh's page is dropped; then the resync's fetch fails
        let failResync!: (error: unknown) => void;
        api.onFetch = () => new Promise((_, reject) => failResync = reject);
        api.streams[0].emit({kind: "resync"});
        answerRefresh(page(["a"]));
        await vi.advanceTimersByTimeAsync(0);
        expect(controller.state.feed.loaded).toBe(false);
        expect(controller.state.loadError).toBeNull();

        failResync(new FeedRequestError("Too many requests."));
        await vi.advanceTimersByTimeAsync(0);

        expect(controller.state.feed.loaded).toBe(false);
        expect(controller.state.loadError).toBe("Too many requests.");

        api.onFetch = async () => page(["a"]);
        await controller.refresh(true);
        expect(controller.state.loadError).toBeNull();
        expect(ids()).toEqual(["a"]);
    });

    it("shows a first load that fails as failed when the sign-in's refresh answers after the resync failed", async () => {
        session.signInAsDevUser("alice");
        let answerRefresh!: (page: PacketPage) => void;
        api.onFetch = () => new Promise(resolve => answerRefresh = resolve);
        controller.start();

        api.onFetch = async () => {
            throw new FeedRequestError("Couldn't reach Passel.");
        };
        api.streams[0].emit({kind: "resync"});
        await vi.advanceTimersByTimeAsync(0);
        expect(controller.state.loadError).toBe("Couldn't reach Passel.");

        answerRefresh(page(["a"]));
        await vi.advanceTimersByTimeAsync(0);
        expect(controller.state.loadError).toBe("Couldn't reach Passel.");
    });

    it("keeps the feed and tells the user when a refresh they asked for fails", async () => {
        session.signInAsDevUser("alice");
        api.onFetch = async () => page(["a"]);
        controller.start();
        await vi.runOnlyPendingTimersAsync();

        api.onFetch = async () => {
            throw new FeedRequestError("Too many requests.");
        };
        await controller.refresh(true);

        expect(ids()).toEqual(["a"]);
        expect(controller.state.loadError).toBeNull();
        expect(controller.state.notice).toBe("Couldn't refresh: Too many requests.");

        controller.dismissNotice();
        expect(controller.state.notice).toBeNull();
    });

    it("says the session expired when a Google token runs out", async () => {
        session.acceptGoogleCredential(googleToken({}, now), null);
        api.onFetch = async () => page(["a"]);
        controller.start();
        await vi.runOnlyPendingTimersAsync();

        // The token lapses; the next request finds no token and the API client reports the user signed out
        now += 2 * 3600 * 1000;
        expect(session.token()).toBeNull();
        api.onFetch = async () => {
            throw new FeedRequestError("You're not signed in.", true);
        };
        await controller.refresh();

        expect(controller.state.auth).toEqual({status: "signedOut", message: SESSION_EXPIRED_MESSAGE});
        expect(api.openStreams).toEqual([]);
        expect(session.returning).toBe(true);
    });

    it("opens on the sign-in page, saying why, when the tab's token expired while it was closed", () => {
        session.acceptGoogleCredential(googleToken({}, now), null);
        now += 2 * 3600 * 1000;

        create().start();

        expect(controller.state.auth).toEqual({status: "signedOut", message: SESSION_EXPIRED_MESSAGE});
    });

    it("a failure to load older items shows at the end of the feed and is retried on demand", async () => {
        session.signInAsDevUser("alice");
        api.onFetch = async cursor => cursor ? Promise.reject(new FeedRequestError("Nope.")) : page(["a"], "c1");
        controller.start();
        await vi.runOnlyPendingTimersAsync();

        await controller.loadMore();
        expect(controller.state.feed.loadMoreError).toBe("Nope.");

        api.onFetch = async () => page(["b"]);
        await controller.loadMore(true);
        expect(ids()).toEqual(["a", "b"]);
    });
});
