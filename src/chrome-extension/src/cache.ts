import type {Packet} from "./types.js";

// The feed is cached in local storage so the popup can show it instantly (and offline). It belongs to
// the signed-in user: it's cleared on explicit sign-out and when a different account signs in.
const CACHE_KEY = "packetsCache";

export interface CachedFeed {
    packets: Packet[];
    nextCursor: string | null;
    /** When the feed was last fetched (ms since the epoch). */
    lastSyncedAt: number | null;
}

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
