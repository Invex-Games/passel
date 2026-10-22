import type {CachedFeed, Packet} from "@passel/core";

// The feed is cached in local storage so the popup can show it instantly (and offline). It belongs to
// the signed-in user: it is cleared on explicit sign-out and when a different account signs in.
const CACHE_KEY = "packetsCache";

export async function loadFeedCache(): Promise<CachedFeed | null> {
    try {
        const result = await chrome.storage.local.get(CACHE_KEY);
        const cached = result[CACHE_KEY] as CachedFeed | undefined;
        return cached && Array.isArray(cached.packets) ? cached : null;
    } catch {
        return null;
    }
}

export async function saveFeedCache(feed: CachedFeed): Promise<void> {
    await chrome.storage.local.set({[CACHE_KEY]: feed});
}

export async function clearFeedCache(): Promise<void> {
    await chrome.storage.local.remove(CACHE_KEY);
}
