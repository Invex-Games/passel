import {beforeEach, expect, it} from "vitest";
import {clearFeedCache, loadFeedCache, saveFeedCache} from "../src/cache.js";
import {installChrome, type FakeChrome} from "./chrome.js";

const FEED = {
    packets: [{id: "p1", type: "text" as const, title: null, payload: "Milk", isImage: false}],
    nextCursor: "c1",
    lastSyncedAt: 1000,
};

let fake: FakeChrome;

beforeEach(() => {
    fake = installChrome();
});

it("keeps the feed in local storage until it's cleared", async () => {
    await saveFeedCache(FEED);
    expect(fake.storage.local.data.packetsCache).toEqual(FEED);
    expect(await loadFeedCache()).toEqual(FEED);

    await clearFeedCache();
    expect(await loadFeedCache()).toBeNull();
});

it("ignores a cache it can't read, rather than failing the popup", async () => {
    fake.storage.local.data.packetsCache = {packets: "not a list"};
    expect(await loadFeedCache()).toBeNull();

    fake.storage.local.get.mockRejectedValue(new Error("storage is unavailable"));
    expect(await loadFeedCache()).toBeNull();
});
