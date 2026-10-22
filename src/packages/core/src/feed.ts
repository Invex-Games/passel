import {mergePackets} from "./packets.js";
import type {CachedFeed} from "./types.js";
import type {Packet, PacketPage} from "./types.js";

// The popup's packet feed: the list, paging, and keeping it in step with live events and deletions.
// It mirrors the mobile app's PacketsNotifier. Kept free of chrome.* APIs so it can be unit-tested;
// the popup supplies the packet source (the service worker) and the cache (local storage).

/**
 * How many packets the API returns per page, and so how many the cache holds. The cache exists to show
 * something instantly on the next open; older pages are fetched again by load-more, so caching them would
 * only make the cache grow without bound.
 */
export const PAGE_SIZE = 50;

export interface FeedState extends CachedFeed {
    /** Whether the feed has been loaded (from the cache or the API). */
    loaded: boolean;
    loadingMore: boolean;
    /** Why loading older packets last failed; older pages aren't fetched automatically until retried. */
    loadMoreError: string | null;
}

/** A failed request; `signedOut` means the user needs to sign in (again). */
export class FeedRequestError extends Error {
    constructor(message: string, readonly signedOut = false) {
        super(message);
    }
}

export interface FeedSource {
    /** Fetches a page of packets, newest first; throws FeedRequestError on failure. */
    fetchPage(cursor?: string): Promise<PacketPage>;
}

export interface FeedCache {
    load(): Promise<CachedFeed | null>;

    save(feed: CachedFeed): Promise<void>;

    clear(): Promise<void>;
}

interface HiddenPacket {
    packet: Packet;
    index: number;
    newerId: string | null;
    olderId: string | null;
}

const EMPTY: FeedState = {
    packets: [], nextCursor: null, lastSyncedAt: null, loaded: false, loadingMore: false, loadMoreError: null,
};

export class FeedStore {
    private current: FeedState = EMPTY;
    private readonly listeners = new Set<(state: FeedState) => void>();

    /** Bumped whenever the list is replaced wholesale; a loadMore started before is discarded. */
    private generation = 0;

    /** Sequence number of the latest first-page fetch; an older one that completes late is dropped. */
    private fetchSeq = 0;

    /**
     * Packets added outside a fetch (sent, or received live), oldest first. A fetch that was in flight
     * when one arrived may predate it, so the ones that arrived during the fetch are merged back on top.
     */
    private arrivals: Packet[] = [];

    /**
     * Ids of deleted packets, including ones only hidden while the deletion can be undone. A fetch may
     * have been served before the deletion, so these are removed from anything fetched.
     */
    private readonly deletedIds = new Set<string>();

    /** Packets hidden by hide() and where they were, so unhide() can put them back. */
    private readonly hidden = new Map<string, HiddenPacket>();

    /**
     * The cursor that followed the first page when it was last fetched. The cache holds only the first
     * page, so it's saved with this cursor rather than the one after the last page loaded.
     */
    private firstPageCursor: string | null = null;

    constructor(
        private readonly source: FeedSource,
        private readonly cache: FeedCache,
        private readonly now: () => number = Date.now,
    ) {
    }

    get state(): FeedState {
        return this.current;
    }

    subscribe(listener: (state: FeedState) => void): () => void {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    }

    /** Shows the cached feed, if there is one and nothing is loaded yet. */
    async showCached(): Promise<void> {
        const cached = await this.cache.load();
        if (!cached || this.current.loaded) return;

        this.firstPageCursor = cached.nextCursor;
        this.set({...EMPTY, ...cached, loaded: true});
    }

    /** Forgets the feed and its cache (sign-out, or a different account signing in). */
    async clear(): Promise<void> {
        this.generation++;
        this.fetchSeq++;
        this.arrivals = [];
        this.deletedIds.clear();
        this.hidden.clear();
        this.firstPageCursor = null;
        this.set(EMPTY);
        await this.cache.clear().catch(() => {
        });
    }

    /** Replaces the list with the first page. Throws FeedRequestError if the request fails. */
    async refresh(): Promise<void> {
        const seq = ++this.fetchSeq;
        const arrivalsMark = this.arrivals.length;
        const page = await this.source.fetchPage();
        if (seq !== this.fetchSeq) return;

        this.replace(page, arrivalsMark);
    }

    /**
     * Fetches the first page and merges it into the top of the list, keeping older pages already loaded.
     * Used to catch up when the live stream (re)connects. If the page doesn't overlap the list, too much
     * changed to stitch them together and the list is replaced instead. Throws if the request fails.
     */
    async syncLatest(): Promise<void> {
        const seq = ++this.fetchSeq;
        const arrivalsMark = this.arrivals.length;
        const page = await this.source.fetchPage();
        if (seq !== this.fetchSeq) return;

        const latest = this.current;
        const packets = this.reconcile(arrivalsMark, page.packets);
        const listedIds = new Set(latest.packets.map(p => p.id));

        if (!latest.loaded || !packets.some(p => listedIds.has(p.id))) {
            this.replace(page, arrivalsMark);
            return;
        }

        this.firstPageCursor = page.nextCursor;
        this.commit({...latest, packets: mergePackets(packets, latest.packets), lastSyncedAt: this.now()});
    }

    /**
     * Loads the next page and appends it. Does nothing while a previous failure is pending unless `retry`
     * is true, so scrolling doesn't hammer a failing endpoint. Throws FeedRequestError if the request fails.
     */
    async loadMore(retry = false): Promise<void> {
        const current = this.current;
        const cursor = current.nextCursor;
        if (!current.loaded || !cursor || current.loadingMore) return;
        if (current.loadMoreError && !retry) return;

        const generation = this.generation;
        this.set({...current, loadingMore: true, loadMoreError: null});

        try {
            const page = await this.source.fetchPage(cursor);
            // The list was replaced while loading: this page is stale.
            if (generation !== this.generation) return;

            const latest = this.current;
            this.commit({
                ...latest,
                packets: mergePackets(latest.packets, this.withoutDeleted(page.packets)),
                // Defensive: a server echoing the same cursor would loop forever.
                nextCursor: page.nextCursor === cursor ? null : page.nextCursor,
                loadingMore: false,
            });
        } catch (err) {
            if (generation === this.generation) {
                this.set({...this.current, loadingMore: false, loadMoreError: (err as Error).message});
            }
            throw err;
        }
    }

    /** Adds a packet that was just sent, or received from the live stream, to the top of the list. */
    applyCreated(packet: Packet): void {
        if (this.deletedIds.has(packet.id)) return;
        this.arrivals.push(packet);

        const latest = this.current;
        if (!latest.loaded || latest.packets.some(p => p.id === packet.id)) return;
        this.commit({...latest, packets: mergePackets([packet], latest.packets)});
    }

    /** Removes a packet that was deleted (elsewhere, or here once the deletion went through). */
    applyDeleted(id: string): void {
        this.deletedIds.add(id);
        // It's gone for good: an undo can't bring it back.
        this.hidden.delete(id);

        const latest = this.current;
        if (!latest.packets.some(p => p.id === id)) return;
        this.commit({...latest, packets: latest.packets.filter(p => p.id !== id)});
    }

    /**
     * Removes a packet from the list without deleting it, so the deletion can still be undone with
     * unhide(). Returns false if the packet isn't listed.
     */
    hide(id: string): boolean {
        const latest = this.current;
        const index = latest.packets.findIndex(p => p.id === id);
        if (index === -1) return false;

        this.hidden.set(id, {
            packet: latest.packets[index],
            index,
            newerId: latest.packets[index - 1]?.id ?? null,
            olderId: latest.packets[index + 1]?.id ?? null,
        });
        this.deletedIds.add(id);
        this.commit({...latest, packets: latest.packets.filter(p => p.id !== id)});
        return true;
    }

    /** Puts a packet hidden by hide() back where it was (unless it was deleted in the meantime). */
    unhide(id: string): void {
        const hidden = this.hidden.get(id);
        if (!hidden) return;
        this.hidden.delete(id);
        this.deletedIds.delete(id);

        const latest = this.current;
        if (latest.packets.some(p => p.id === id)) return;

        // Packets may have arrived or been loaded since, so prefer going back next to the same
        // neighbours over the same index.
        const ids = latest.packets.map(p => p.id);
        const olderIndex = hidden.olderId ? ids.indexOf(hidden.olderId) : -1;
        const newerIndex = hidden.newerId ? ids.indexOf(hidden.newerId) : -1;
        const index = olderIndex !== -1 ? olderIndex
            : newerIndex !== -1 ? newerIndex + 1
                : Math.min(hidden.index, ids.length);

        const packets = [...latest.packets];
        packets.splice(index, 0, hidden.packet);
        this.commit({...latest, packets});
    }

    // ── Private ──────────────────────────────────────────

    private replace(page: PacketPage, arrivalsMark: number): void {
        this.generation++;
        this.firstPageCursor = page.nextCursor;
        this.commit({
            ...this.current,
            packets: this.reconcile(arrivalsMark, page.packets),
            nextCursor: page.nextCursor,
            lastSyncedAt: this.now(),
            loaded: true,
            loadingMore: false,
            loadMoreError: null,
        });
    }

    /**
     * Brings fetched packets up to date with what happened while they were in flight: packets that arrived
     * since `arrivalsMark` are merged on top (newest first) and deleted packets are removed.
     */
    private reconcile(arrivalsMark: number, fetched: readonly Packet[]): Packet[] {
        const arrived = this.arrivals.slice(arrivalsMark).reverse();
        return this.withoutDeleted(mergePackets(arrived, fetched));
    }

    private withoutDeleted(packets: readonly Packet[]): Packet[] {
        return packets.filter(p => !this.deletedIds.has(p.id));
    }

    /**
     * Updates the state and saves it to the cache. The in-memory list keeps every page loaded; the cache
     * gets only the first page and the cursor after it, so local storage never grows with load-more.
     */
    private commit(state: FeedState): void {
        this.set(state);
        this.cache
            .save({
                packets: state.packets.slice(0, PAGE_SIZE),
                nextCursor: this.firstPageCursor,
                lastSyncedAt: state.lastSyncedAt,
            })
            .catch(() => {
                // The cache only speeds up the next open; failing to write it isn't worth surfacing.
            });
    }

    private set(state: FeedState): void {
        this.current = state;
        for (const listener of this.listeners) listener(state);
    }
}
