import {beforeEach, describe, expect, it} from "vitest";
import {FeedRequestError, FeedStore, PAGE_SIZE, type FeedCache} from "../src/feed.js";
import type {CachedFeed} from "../src/types.js";
import type {Packet, PacketPage} from "../src/types.js";

const text = (id: string): Packet => ({id, type: "text", title: null, payload: `body ${id}`, isImage: false});
const page = (ids: string[], nextCursor: string | null = null): PacketPage => ({packets: ids.map(text), nextCursor});
/** Ids for a full page: `${prefix}1` to `${prefix}50`. */
const fullPage = (prefix: string) => Array.from({length: PAGE_SIZE}, (_, i) => `${prefix}${i + 1}`);

/** A promise the test resolves or rejects by hand. */
function deferred<T>() {
    let resolve!: (value: T) => void;
    let reject!: (reason: unknown) => void;
    const promise = new Promise<T>((res, rej) => {
        resolve = res;
        reject = rej;
    });
    return {promise, resolve, reject};
}

class MemoryCache implements FeedCache {
    feed: CachedFeed | null = null;

    async load() {
        return this.feed;
    }

    async save(feed: CachedFeed) {
        this.feed = feed;
    }

    async clear() {
        this.feed = null;
    }
}

let onFetch: (cursor?: string) => Promise<PacketPage>;
let cursors: (string | undefined)[];
let cache: MemoryCache;
let feed: FeedStore;

const ids = () => feed.state.packets.map(p => p.id);

beforeEach(() => {
    onFetch = async () => page([]);
    cursors = [];
    cache = new MemoryCache();
    feed = new FeedStore({
        fetchPage: cursor => {
            cursors.push(cursor);
            return onFetch(cursor);
        },
    }, cache, () => 1000);
});

describe("loading", () => {
    it("shows the cache, then replaces it with the first page and saves it", async () => {
        cache.feed = {packets: [text("cached")], nextCursor: null, lastSyncedAt: 1};
        await feed.showCached();
        expect(ids()).toEqual(["cached"]);

        onFetch = async () => page(["a", "b"], "c1");
        await feed.refresh();

        expect(ids()).toEqual(["a", "b"]);
        expect(feed.state.nextCursor).toBe("c1");
        expect(feed.state.lastSyncedAt).toBe(1000);
        expect(cache.feed).toEqual({packets: [text("a"), text("b")], nextCursor: "c1", lastSyncedAt: 1000});
    });

    it("a slower, older refresh does not overwrite a newer one", async () => {
        const slow = deferred<PacketPage>();
        let calls = 0;
        onFetch = () => ++calls === 1 ? slow.promise : Promise.resolve(page(["newest"]));

        const older = feed.refresh();
        await feed.refresh();
        slow.resolve(page(["stale"]));
        await older;

        expect(ids()).toEqual(["newest"]);
    });

    it("propagates failures and keeps the existing list", async () => {
        onFetch = async () => page(["a"]);
        await feed.refresh();
        onFetch = async () => {
            throw new FeedRequestError("offline");
        };

        await expect(feed.refresh()).rejects.toThrow("offline");
        expect(ids()).toEqual(["a"]);
    });

    it("clear forgets the feed and its cache", async () => {
        onFetch = async () => page(["a"]);
        await feed.refresh();

        await feed.clear();

        expect(feed.state.loaded).toBe(false);
        expect(ids()).toEqual([]);
        expect(cache.feed).toBeNull();
    });
});

describe("loadMore", () => {
    it("appends the next page and de-duplicates", async () => {
        onFetch = async cursor => cursor ? page(["b", "c"], "c2") : page(["a", "b"], "c1");
        await feed.refresh();

        await feed.loadMore();

        expect(cursors).toEqual([undefined, "c1"]);
        expect(ids()).toEqual(["a", "b", "c"]);
        expect(feed.state.nextCursor).toBe("c2");
    });

    it("discards a stale page when the list was replaced meanwhile", async () => {
        const page2 = deferred<PacketPage>();
        let firstPages = 0;
        onFetch = cursor => cursor
            ? page2.promise
            : Promise.resolve(++firstPages === 1 ? page(["a"], "c1") : page(["x"], "c9"));
        await feed.refresh();

        const loading = feed.loadMore();
        expect(feed.state.loadingMore).toBe(true);
        await feed.refresh();
        page2.resolve(page(["old"]));
        await loading;

        expect(ids()).toEqual(["x"]);
        expect(feed.state.nextCursor).toBe("c9");
        expect(feed.state.loadingMore).toBe(false);
    });

    it("remembers a failure, stops auto-retrying, and retries on demand", async () => {
        let fail = true;
        onFetch = async cursor => {
            if (!cursor) return page(["a"], "c1");
            if (fail) throw new FeedRequestError("offline");
            return page(["b"]);
        };
        await feed.refresh();

        await expect(feed.loadMore()).rejects.toThrow("offline");
        expect(feed.state.loadMoreError).toBe("offline");

        await feed.loadMore();
        expect(cursors).toEqual([undefined, "c1"]);

        fail = false;
        await feed.loadMore(true);
        expect(ids()).toEqual(["a", "b"]);
        expect(feed.state.loadMoreError).toBeNull();
    });

    it("stops paging if the server repeats the same cursor", async () => {
        onFetch = async cursor => cursor ? page(["b"], "same") : page(["a"], "same");
        await feed.refresh();

        await feed.loadMore();

        expect(feed.state.nextCursor).toBeNull();
    });
});

describe("live updates", () => {
    it("syncLatest merges the first page on top, keeping older pages", async () => {
        let firstPages = 0;
        onFetch = async cursor => cursor === "c1"
            ? page(["c"], "c2")
            : ++firstPages === 1 ? page(["a", "b"], "c1") : page(["new", "a"], "x");
        await feed.refresh();
        await feed.loadMore();

        await feed.syncLatest();

        expect(ids()).toEqual(["new", "a", "b", "c"]);
        expect(feed.state.nextCursor).toBe("c2");
        // The cache holds the first page, so it's saved with the cursor that followed the fresh first page.
        expect(cache.feed?.nextCursor).toBe("x");
    });

    it("syncLatest replaces the list when the page doesn't overlap it", async () => {
        let calls = 0;
        onFetch = async () => ++calls === 1 ? page(["a"], "c1") : page(["x", "y"], "c9");
        await feed.refresh();

        await feed.syncLatest();

        expect(ids()).toEqual(["x", "y"]);
        expect(feed.state.nextCursor).toBe("c9");
    });

    it("applyCreated prepends a packet once", async () => {
        onFetch = async () => page(["a"]);
        await feed.refresh();

        feed.applyCreated(text("live"));
        feed.applyCreated(text("live"));

        expect(ids()).toEqual(["live", "a"]);
        expect(cache.feed?.packets[0].id).toBe("live");
    });

    it("keeps a packet that arrived while a fetch predating it was in flight", async () => {
        const pending = deferred<PacketPage>();
        let calls = 0;
        onFetch = () => ++calls === 1 ? Promise.resolve(page(["a"])) : pending.promise;
        await feed.refresh();

        const refresh = feed.refresh();
        feed.applyCreated(text("live"));
        pending.resolve(page(["new", "a"]));
        await refresh;

        expect(ids()).toEqual(["live", "new", "a"]);
    });

    it("applyDeleted removes the packet and keeps it out of later fetches", async () => {
        let calls = 0;
        onFetch = async () => ++calls === 1 ? page(["a", "b"]) : page(["a", "b"]);
        await feed.refresh();

        feed.applyDeleted("a");
        expect(ids()).toEqual(["b"]);

        await feed.refresh();
        expect(ids()).toEqual(["b"]);

        feed.applyCreated(text("a"));
        expect(ids()).toEqual(["b"]);
    });
});

describe("delete with undo", () => {
    beforeEach(async () => {
        onFetch = async () => page(["a", "b", "c"]);
        await feed.refresh();
    });

    it("hide removes the packet until unhide puts it back in place", () => {
        expect(feed.hide("b")).toBe(true);
        expect(ids()).toEqual(["a", "c"]);
        expect(cache.feed?.packets.map(p => p.id)).toEqual(["a", "c"]);

        feed.unhide("b");
        expect(ids()).toEqual(["a", "b", "c"]);
    });

    it("hide returns false for a packet that isn't listed", () => {
        expect(feed.hide("x")).toBe(false);
    });

    it("unhide goes back next to the same neighbours after new packets arrive", () => {
        feed.hide("b");
        feed.applyCreated(text("new"));

        feed.unhide("b");

        expect(ids()).toEqual(["new", "a", "b", "c"]);
    });

    it("a hidden packet stays hidden when a refresh brings it back", async () => {
        feed.hide("b");

        await feed.refresh();

        expect(ids()).toEqual(["a", "c"]);
    });

    it("a packet deleted elsewhere can't be brought back by undo", () => {
        feed.hide("b");
        feed.applyDeleted("b");

        feed.unhide("b");

        expect(ids()).toEqual(["a", "c"]);
    });
});

describe("cache", () => {
    const cachedIds = () => cache.feed?.packets.map(p => p.id);

    it("saves only the first page and its cursor, however many pages are loaded", async () => {
        onFetch = async cursor => cursor ? page(fullPage("old"), "c2") : page(fullPage("p"), "c1");
        await feed.refresh();

        await feed.loadMore();

        expect(ids()).toHaveLength(2 * PAGE_SIZE);
        expect(feed.state.nextCursor).toBe("c2");
        expect(cachedIds()).toEqual(fullPage("p"));
        expect(cache.feed?.nextCursor).toBe("c1");
    });

    it("a packet that arrives pushes the oldest one out of the cache, keeping the first page's cursor", async () => {
        onFetch = async () => page(fullPage("p"), "c1");
        await feed.refresh();

        feed.applyCreated(text("live"));

        expect(ids()).toHaveLength(PAGE_SIZE + 1);
        expect(cachedIds()).toEqual(["live", ...fullPage("p").slice(0, -1)]);
        expect(cache.feed?.nextCursor).toBe("c1");
    });

    it("keeps the cached cursor when a change is saved before the first fetch", async () => {
        cache.feed = {packets: [text("a")], nextCursor: "c0", lastSyncedAt: 1};
        await feed.showCached();

        feed.applyCreated(text("live"));

        expect(cachedIds()).toEqual(["live", "a"]);
        expect(cache.feed?.nextCursor).toBe("c0");
    });
});
