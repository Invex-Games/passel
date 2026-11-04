import {FeedRequestError, FeedStore, tryParseHttpUrl} from "@passel/core";
import type {
    CancelDeleteResponse,
    DeletePacketResponse,
    FeedState,
    GetPacketsResponse,
    Packet,
} from "@passel/core";
import {clearFeedCache, loadFeedCache, saveFeedCache} from "../cache.js";
import {
    feedEnd,
    feedList,
    icon,
    iconButton,
    mainEl,
    refreshBtn,
    sendMessage,
    spinner,
    syncLabelEl,
    textButton,
    textEl,
    type IconName,
} from "./dom.js";
import {showError, showToast} from "./toast.js";

// The feed as the popup shows it: the tiles, the row after the oldest packet, the sync label, paging,
// copying and deleting with undo. The feed's rules live in @passel/core's FeedStore; this is the DOM on top.

/** How long a deletion can be undone before it's sent. */
const UNDO_DELETE_MS = 4000;

/** Older packets start loading once the end of the feed is this close to being scrolled into view. */
const LOAD_MORE_THRESHOLD_PX = 200;

/** Where the copy menu opens, measured from the top of its tile: just under the tile's header. */
const MENU_TOP_PX = 40;

export interface FeedViewDeps {
    isSignedIn(): boolean;
    isLive(): boolean;
    /** A request failed for lack of a session: the controller signs the popup out. */
    onSignedOut(): void;
}

export interface FeedView {
    readonly feed: FeedStore;
    /** Shows the cached feed, before the session is known. */
    showCached(): Promise<void>;
    refresh(manual: boolean): Promise<void>;
    syncLatest(): void;
    /** Adds a packet that just arrived (sent here, or received live); its tile animates in. */
    applyArrived(packet: Packet): void;
    applyDeleted(id: string): void;
    /** Forgets the feed, for a sign-out or a change of account. */
    clear(): Promise<void>;
    render(): void;
    updateSyncLabel(): void;
}

export function createFeedView(deps: FeedViewDeps): FeedView {
    /** Why the first load failed, while there's nothing (not even a cache) to show instead. */
    let loadError: string | null = null;

    /** Tiles by packet id. Packets never change, so a tile is built once and reused. */
    const tiles = new Map<string, HTMLElement>();

    /** Ids of packets that arrived since the last render, so their tiles slide in. */
    const arrivedIds = new Set<string>();

    let openMenu: HTMLElement | null = null;

    const feed = new FeedStore(
        {
            async fetchPage(cursor) {
                const response = await sendMessage<GetPacketsResponse>({action: "getPackets", cursor});
                if (response?.success && response.page) return response.page;
                throw new FeedRequestError(response?.error || "Couldn't load packets.", response?.signedOut === true);
            },
        },
        {load: loadFeedCache, save: saveFeedCache, clear: clearFeedCache},
    );

    feed.subscribe(renderFeed);

    async function refresh(manual: boolean): Promise<void> {
        setRefreshing(true);
        try {
            await feed.refresh();
            loadError = null;
        } catch (err) {
            if (!feed.state.loaded) loadError = (err as Error).message;
            handleFeedError(err, manual ? "Refresh failed" : null);
        } finally {
            setRefreshing(false);
            renderFeed(feed.state);
        }
    }

    async function loadMore(retry = false): Promise<void> {
        try {
            await feed.loadMore(retry);
        } catch (err) {
            // The failure is shown in the feed (with a retry button) via the feed state.
            handleFeedError(err, null);
        }
    }

    /** Signs the popup out if the request failed for lack of a session; otherwise optionally shows the error. */
    function handleFeedError(err: unknown, toastPrefix: string | null): void {
        if (err instanceof FeedRequestError && err.signedOut) {
            deps.onSignedOut();
            showError(err.message);
            return;
        }
        if (toastPrefix) showToast(`${toastPrefix}: ${(err as Error).message}`);
    }

    function maybeLoadMore(): void {
        const state = feed.state;
        if (!deps.isSignedIn() || !state.loaded || !state.nextCursor || state.loadingMore || state.loadMoreError) return;

        const distanceToEnd = mainEl.scrollHeight - mainEl.scrollTop - mainEl.clientHeight;
        if (distanceToEnd <= LOAD_MORE_THRESHOLD_PX) void loadMore();
    }

    mainEl.addEventListener("scroll", maybeLoadMore, {passive: true});
    refreshBtn.addEventListener("click", () => void refresh(true));

    function syncLatest(): void {
        feed.syncLatest().catch(err => handleFeedError(err, null));
    }

    function applyArrived(packet: Packet): void {
        arrivedIds.add(packet.id);
        feed.applyCreated(packet);
    }

    // ── Rendering ─────────────────────────────────────────

    function renderFeed(state: FeedState): void {
        const elements = state.packets.map(packet => {
            let tile = tiles.get(packet.id);
            if (!tile) {
                tile = createTile(packet, arrivedIds.has(packet.id));
                tiles.set(packet.id, tile);
            }
            return tile;
        });
        arrivedIds.clear();

        const listed = new Set(state.packets.map(p => p.id));
        for (const id of tiles.keys()) {
            if (!listed.has(id)) tiles.delete(id);
        }

        // Only touch the DOM when the list changed, so tiles (and an open menu) aren't needlessly re-inserted.
        const children = feedList.children;
        const unchanged = children.length === elements.length && elements.every((el, i) => children[i] === el);
        if (!unchanged) {
            // The menu is positioned against the tile it belongs to, which is about to move.
            closeMenu();
            feedList.replaceChildren(...elements);
        }

        renderFeedEnd(state);
        updateSyncLabel();

        requestAnimationFrame(() => {
            measureClampedTiles();
            maybeLoadMore();
        });
    }

    /** The slot after the oldest packet: loading/retry/sign-in prompts, or the end of the feed. */
    function renderFeedEnd(state: FeedState): void {
        const signedIn = deps.isSignedIn();
        feedEnd.className = "feed-end";
        feedEnd.replaceChildren();

        if (!state.loaded) {
            if (loadError) {
                feedEnd.classList.add("error");
                feedEnd.append(textEl("span", "", loadError), textButton("Retry", () => void refresh(true)));
            } else if (signedIn) {
                feedEnd.append(spinner());
            } else {
                feedEnd.append(textEl("span", "", "Sign in to see your links and notes"));
            }
            return;
        }

        if (state.nextCursor) {
            if (state.loadMoreError && !state.loadingMore) {
                feedEnd.classList.add("error");
                feedEnd.append(
                    textEl("span", "", "Couldn't load older items."),
                    textButton("Retry", () => void loadMore(true)),
                );
            } else if (!signedIn) {
                feedEnd.append(textEl("span", "", "Sign in to load older items"));
            } else {
                feedEnd.append(spinner());
            }
            return;
        }

        if (state.packets.length === 0) {
            feedEnd.append(textEl("span", "", signedIn
                ? "Nothing here yet. Send a link or a note above, or from another device."
                : "Sign in to see your links and notes"));
            return;
        }

        feedEnd.append(textEl("span", "", "You've gone back to the beginning!"));
    }

    function createTile(packet: Packet, arrived: boolean): HTMLElement {
        const tile = document.createElement("article");
        tile.className = "packet";

        if (arrived) {
            // Only live arrivals slide in; a cached or fetched render would otherwise animate every tile at once.
            // The class goes once the animation ends, so re-inserting the tile when the list is reordered doesn't replay it.
            tile.classList.add("arrived");
            tile.addEventListener("animationend", e => {
                if (e.target === tile) tile.classList.remove("arrived");
            });
        }

        const link = packet.type === "link" ? tryParseHttpUrl(packet.payload) : null;

        const header = document.createElement("div");
        header.className = "packet-header";
        header.append(icon(typeIcon(packet)), textEl("span", "packet-title", packetTitle(packet)));

        if (link) header.append(iconButton("external", "Open link", () => void chrome.tabs.create({url: link.href})));
        if (packet.type !== "file") header.append(iconButton("copy", "Copy", () => copyPacket(packet, tile)));

        const deleteBtn = iconButton("trash", "Delete", () => void deleteWithUndo(packet));
        deleteBtn.classList.add("packet-delete");
        header.append(deleteBtn);

        const body = document.createElement("div");
        body.className = "packet-body";

        if (packet.type === "file") {
            body.append(textEl("span", "packet-note", "Files aren't supported yet."));
        } else if (link) {
            // Only http(s) links are clickable; anything else is shown as plain text.
            const anchor = document.createElement("a");
            anchor.href = link.href;
            anchor.target = "_blank";
            anchor.rel = "noopener noreferrer";
            anchor.textContent = packet.payload;
            body.append(anchor);
        } else {
            body.textContent = packet.payload;
            // Long text is clamped; measureClampedTiles() adds "Show more" if it overflows.
            body.classList.add("clamped");
        }

        tile.append(header, body);
        return tile;
    }

    /**
     * Adds a "Show more" button to newly shown clamped text that overflows, or unclamps text that fits.
     * Every body is measured before any is changed, so the feed is laid out once rather than once per tile.
     */
    function measureClampedTiles(): void {
        const bodies = [...feedList.querySelectorAll<HTMLElement>(".packet-body.clamped:not([data-measured])")];
        const overflowing = bodies.map(body => body.scrollHeight > body.clientHeight + 1);

        bodies.forEach((body, i) => {
            body.dataset.measured = "true";
            if (!overflowing[i]) {
                body.classList.remove("clamped");
                return;
            }

            const more = textButton("Show more", () => {
                body.classList.remove("clamped");
                more.remove();
            });
            more.classList.add("show-more");
            body.after(more);
        });
    }

    // ── Copy ──────────────────────────────────────────────

    /**
     * Copies a packet's content. When it has a title as well, a menu lets the user choose
     * between the title, the content, or both (like the mobile app).
     */
    function copyPacket(packet: Packet, tile: HTMLElement): void {
        if (!packet.title) {
            void copyText(packet.payload);
            return;
        }

        closeMenu();
        const menu = document.createElement("div");
        menu.className = "menu";
        menu.setAttribute("role", "menu");

        const options: [string, string | null, string][] = [
            ["Copy title", packet.title, packet.title],
            ["Copy content", packet.payload, packet.payload],
            ["Copy both", null, `${packet.title}\n${packet.payload}`],
        ];
        for (const [label, detail, text] of options) {
            const item = document.createElement("button");
            item.type = "button";
            item.setAttribute("role", "menuitem");
            item.append(textEl("span", "", label));
            if (detail) item.append(textEl("span", "menu-detail", detail));
            item.addEventListener("click", () => {
                closeMenu();
                void copyText(text);
            });
            menu.append(item);
        }

        // The menu hangs below the tile's header but lives in the list: a tile contains its paint (see .packet in
        // popup.css), so a menu inside it would be clipped at the tile's edge and painted under the next tile.
        menu.style.top = `${tile.offsetTop + MENU_TOP_PX}px`;
        feedList.append(menu);
        openMenu = menu;
        (menu.firstElementChild as HTMLElement).focus();
    }

    function closeMenu(): void {
        openMenu?.remove();
        openMenu = null;
    }

    document.addEventListener("click", e => {
        if (openMenu && !openMenu.contains(e.target as Node) && !(e.target as Element).closest?.("[title='Copy']")) {
            closeMenu();
        }
    });

    document.addEventListener("keydown", e => {
        if (e.key === "Escape" && openMenu) {
            e.preventDefault();
            closeMenu();
        }
    });

    async function copyText(text: string): Promise<void> {
        try {
            await navigator.clipboard.writeText(text);
            showToast("Copied to clipboard");
        } catch {
            showToast("Couldn't copy to the clipboard");
        }
    }

    // ── Delete with undo ──────────────────────────────────

    /**
     * Removes the packet from the feed and shows an "Undo" toast. The service worker sends the deletion once
     * the undo window has passed, so it still happens if the popup is closed in the meantime.
     */
    async function deleteWithUndo(packet: Packet): Promise<void> {
        if (!feed.hide(packet.id)) return;

        showToast(`${packetNoun(packet)} deleted`, {
            action: {
                label: "Undo",
                onClick: async () => {
                    const response = await sendMessage<CancelDeleteResponse>({action: "cancelDelete", id: packet.id});
                    // Too late if it was already sent; the deletion's own response then removes it for good.
                    if (response?.cancelled) feed.unhide(packet.id);
                },
            },
            durationMs: UNDO_DELETE_MS,
        });

        const response = await sendMessage<DeletePacketResponse>({
            action: "deletePacket", id: packet.id, delayMs: UNDO_DELETE_MS,
        }).catch(() => null);

        if (response?.cancelled) return;
        if (response?.success) {
            feed.applyDeleted(packet.id);
            return;
        }

        feed.unhide(packet.id);
        if (response?.signedOut) deps.onSignedOut();
        showToast(`Couldn't delete the ${packetNoun(packet).toLowerCase()}: ${response?.error || "please try again."}`);
    }

    // ── Sync label ────────────────────────────────────────

    function setRefreshing(value: boolean): void {
        refreshBtn.disabled = value;
        refreshBtn.classList.toggle("spinning", value);
    }

    function updateSyncLabel(): void {
        const live = deps.isLive();
        const text = live ? "Live" : syncLabel(feed.state.lastSyncedAt);
        syncLabelEl.textContent = text ?? "";
        syncLabelEl.classList.toggle("hidden", !text || !deps.isSignedIn() && !feed.state.loaded);
        syncLabelEl.classList.toggle("live", live);
    }

    // Keeps "Synced X ago" current while the popup stays open.
    setInterval(updateSyncLabel, 60_000);

    return {
        feed,
        showCached: () => feed.showCached(),
        refresh,
        syncLatest,
        applyArrived,
        applyDeleted: id => feed.applyDeleted(id),
        // The list goes at once; only the cache is waited for.
        async clear() {
            loadError = null;
            await feed.clear();
        },
        render: () => renderFeed(feed.state),
        updateSyncLabel,
    };
}

function packetTitle(packet: Packet): string {
    return packet.title || packetNoun(packet);
}

/** What the UI calls a packet: a link, a note or a file (ADR 0002). "Packet" stays a code and API term. */
function packetNoun(packet: Packet): string {
    switch (packet.type) {
        case "link":
            return "Link";
        case "text":
            return "Note";
        case "file":
            return "File";
    }
}

function typeIcon(packet: Packet): IconName {
    switch (packet.type) {
        case "link":
            return "link";
        case "text":
            return "text";
        case "file":
            return packet.isImage ? "image" : "file";
    }
}

/** Formats when the feed was last fetched as "Synced X ago". */
function syncLabel(lastSyncedAt: number | null): string | null {
    if (lastSyncedAt === null) return null;

    const seconds = Math.floor((Date.now() - lastSyncedAt) / 1000);
    if (seconds < 60) return "Synced just now";

    const plural = (n: number, unit: string) => `Synced ${n} ${unit}${n === 1 ? "" : "s"} ago`;
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return plural(minutes, "min");
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return plural(hours, "hour");
    return plural(Math.floor(hours / 24), "day");
}
