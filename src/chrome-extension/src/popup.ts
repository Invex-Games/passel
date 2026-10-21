import {clearFeedCache, loadFeedCache, saveFeedCache} from "./cache.js";
import {FeedRequestError, FeedStore, type FeedState} from "./feed.js";
import {packetDataForInput, tryParseHttpUrl} from "./packets.js";
import {FEED_PORT_NAME} from "./types.js";
import type {
    AuthStateResponse,
    CancelDeleteResponse,
    DeletePacketResponse,
    ExtensionMessage,
    FeedPortMessage,
    GetPacketsResponse,
    Packet,
    PacketData,
    SendPacketResponse,
    SignInResponse,
    SignOutResponse,
} from "./types.js";

// All API calls and sign-in are delegated to the service worker, so they complete even if the
// popup closes (e.g. when the Google sign-in window takes focus).

/** How long a deletion can be undone before it's sent. */
const UNDO_DELETE_MS = 4000;

/** How often the popup pings the service worker so it stays alive to forward live updates. */
const KEEP_ALIVE_INTERVAL_MS = 20_000;

/** Older packets start loading once the end of the feed is this close to being scrolled into view. */
const LOAD_MORE_THRESHOLD_PX = 200;

// ── DOM references ────────────────────────────────────────

const mainEl = document.getElementById("main") as HTMLElement;
const sendPageBtn = document.getElementById("send-page") as HTMLButtonElement;
const sendTextBtn = document.getElementById("send-text") as HTMLButtonElement;
const freetextEl = document.getElementById("freetext") as HTMLTextAreaElement;
const statusEl = document.getElementById("status") as HTMLDivElement;
const signInBtn = document.getElementById("sign-in-btn") as HTMLButtonElement;
const signOutBtn = document.getElementById("sign-out-btn") as HTMLButtonElement;
const refreshBtn = document.getElementById("refresh-btn") as HTMLButtonElement;
const syncLabelEl = document.getElementById("sync-label") as HTMLSpanElement;
const authSignedOut = document.getElementById("auth-signed-out") as HTMLElement;
const authSignedIn = document.getElementById("auth-signed-in") as HTMLElement;
const userEmailEl = document.getElementById("user-email") as HTMLSpanElement;
const authRequiredSections = document.querySelectorAll<HTMLElement>(".auth-required");
const feedSection = document.querySelector(".feed") as HTMLElement;
const feedList = document.getElementById("feed-list") as HTMLDivElement;
const feedEnd = document.getElementById("feed-end") as HTMLDivElement;
const toastEl = document.getElementById("toast") as HTMLDivElement;
const toastMessage = document.getElementById("toast-message") as HTMLSpanElement;
const toastAction = document.getElementById("toast-action") as HTMLButtonElement;

let signedIn = false;
let live = false;

/** Why the first load failed, while there's nothing (not even a cache) to show instead. */
let loadError: string | null = null;

function sendMessage<R>(message: ExtensionMessage): Promise<R> {
    return chrome.runtime.sendMessage<ExtensionMessage, R>(message);
}

// ── Feed ──────────────────────────────────────────────────

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

async function refreshFeed(manual: boolean): Promise<void> {
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
        setAuthState({signedIn: false});
        showStatus(err.message, false);
        return;
    }
    if (toastPrefix) showToast(`${toastPrefix}: ${(err as Error).message}`);
}

function maybeLoadMore(): void {
    const state = feed.state;
    if (!signedIn || !state.loaded || !state.nextCursor || state.loadingMore || state.loadMoreError) return;

    const distanceToEnd = mainEl.scrollHeight - mainEl.scrollTop - mainEl.clientHeight;
    if (distanceToEnd <= LOAD_MORE_THRESHOLD_PX) void loadMore();
}

mainEl.addEventListener("scroll", maybeLoadMore, {passive: true});
refreshBtn.addEventListener("click", () => void refreshFeed(true));

// ── Live updates ──────────────────────────────────────────

let port: chrome.runtime.Port | null = null;
let keepAliveTimer: ReturnType<typeof setInterval> | undefined;
let reconnectTimer: ReturnType<typeof setTimeout> | undefined;

function connectLive(): void {
    if (port) return;
    clearTimeout(reconnectTimer);

    const connected = chrome.runtime.connect({name: FEED_PORT_NAME});
    port = connected;
    connected.onMessage.addListener(onPortMessage);
    connected.onDisconnect.addListener(() => {
        // The service worker restarted; reconnect so live updates resume.
        if (port !== connected) return;
        port = null;
        clearInterval(keepAliveTimer);
        setLive(false);
        if (signedIn) reconnectTimer = setTimeout(connectLive, 1000);
    });
    keepAliveTimer = setInterval(() => port?.postMessage({type: "ping"}), KEEP_ALIVE_INTERVAL_MS);
}

function disconnectLive(): void {
    clearTimeout(reconnectTimer);
    clearInterval(keepAliveTimer);
    const connected = port;
    port = null;
    connected?.disconnect();
    setLive(false);
}

function onPortMessage(message: FeedPortMessage): void {
    switch (message.type) {
        case "status":
            setLive(message.live);
            break;

        case "signedOut":
            setAuthState({signedIn: false});
            break;

        case "event":
            switch (message.event.kind) {
                case "resync":
                    // Catch up on anything created or deleted while not connected.
                    feed.syncLatest().catch(err => handleFeedError(err, null));
                    break;
                case "created":
                    feed.applyCreated(message.event.packet);
                    break;
                case "deleted":
                    feed.applyDeleted(message.event.id);
                    break;
            }
            break;
    }
}

function setLive(value: boolean): void {
    live = value;
    updateSyncLabel();
}

// ── Auth state ────────────────────────────────────────────

function setAuthState(state: AuthStateResponse): void {
    const wasSignedIn = signedIn;
    signedIn = state.signedIn;
    userEmailEl.textContent = state.email || "Signed in";

    authSignedOut.classList.toggle("hidden", signedIn);
    authSignedIn.classList.toggle("hidden", !signedIn);
    refreshBtn.classList.toggle("hidden", !signedIn);
    authRequiredSections.forEach(s => s.classList.toggle("disabled-section", !signedIn));
    feedSection.classList.toggle("signed-out", !signedIn);

    freetextEl.disabled = !signedIn;
    updateSendButtons();

    if (signedIn && !wasSignedIn) {
        connectLive();
        void refreshFeed(false);
    } else if (!signedIn && wasSignedIn) {
        disconnectLive();
    }
    renderFeed(feed.state);
}

void (async () => {
    // Show the cached feed straight away; checking the session can take a moment.
    await feed.showCached();
    const state = await sendMessage<AuthStateResponse>({action: "getAuthState"}).catch(() => null);
    setAuthState(state ?? {signedIn: false});
})();

signInBtn.addEventListener("click", async () => {
    setLoading(signInBtn, true);
    try {
        const response = await sendMessage<SignInResponse>({action: "signIn"});
        if (response?.success) {
            // The account picker may have switched accounts, so don't keep showing the previous feed.
            await feed.clear();
            loadError = null;
            setAuthState({signedIn: true, email: response.email});
            showStatus("Signed in!", true);
        } else {
            showStatus(response?.error || "Sign-in failed.", false);
        }
    } catch (err) {
        showStatus(`Error: ${(err as Error).message}`, false);
    } finally {
        setLoading(signInBtn, false);
    }
});

signOutBtn.addEventListener("click", async () => {
    await sendMessage<SignOutResponse>({action: "signOut"});
    setAuthState({signedIn: false});
    await feed.clear();
    loadError = null;
    showStatus("Signed out.", true);
});

// ── Send page ─────────────────────────────────────────────

sendPageBtn.addEventListener("click", async () => {
    setLoading(sendPageBtn, true);
    try {
        const [tab] = await chrome.tabs.query({active: true, currentWindow: true});
        if (!tab?.url) {
            showStatus("Could not get current tab.", false);
            return;
        }
        await send({$type: "Link", name: tab.title || null, url: tab.url}, "Page sent!");
    } catch (err) {
        showStatus(`Error: ${(err as Error).message}`, false);
    } finally {
        setLoading(sendPageBtn, false);
    }
});

// ── Send text or link ─────────────────────────────────────

freetextEl.addEventListener("input", updateSendButtons);

// Enter sends (like the mobile app); Shift+Enter adds a new line.
freetextEl.addEventListener("keydown", e => {
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
        e.preventDefault();
        if (!sendTextBtn.disabled) sendTextBtn.click();
    }
});

sendTextBtn.addEventListener("click", async () => {
    const text = freetextEl.value.trim();
    if (!text) return;

    setLoading(sendTextBtn, true);
    try {
        const data = packetDataForInput(text);
        if (await send(data, data.$type === "Link" ? "Link sent!" : "Text sent!")) {
            freetextEl.value = "";
        }
    } catch (err) {
        showStatus(`Error: ${(err as Error).message}`, false);
    } finally {
        setLoading(sendTextBtn, false);
    }
});

async function send(data: PacketData, successMessage: string): Promise<boolean> {
    const response = await sendMessage<SendPacketResponse>({action: "sendPacket", data});
    if (response?.signedOut) setAuthState({signedIn: false});

    if (response?.success) {
        if (response.packet) feed.applyCreated(response.packet);
        showStatus(successMessage, true);
        return true;
    }

    showStatus(response?.error || "Failed to send.", false);
    return false;
}

// ── Clipboard paste ───────────────────────────────────────

document.addEventListener("paste", (e: ClipboardEvent) => {
    if (!signedIn || document.activeElement === freetextEl) return;

    const text = e.clipboardData?.getData("text/plain")?.trim();
    if (text) {
        e.preventDefault();
        freetextEl.value = text;
        updateSendButtons();
        freetextEl.focus();
    }
});

// ── Feed rendering ────────────────────────────────────────

/** Tiles by packet id. Packets never change, so a tile is built once and reused. */
const tiles = new Map<string, HTMLElement>();

function renderFeed(state: FeedState): void {
    const elements = state.packets.map(packet => {
        let tile = tiles.get(packet.id);
        if (!tile) {
            tile = createTile(packet);
            tiles.set(packet.id, tile);
        }
        return tile;
    });

    const listed = new Set(state.packets.map(p => p.id));
    for (const id of tiles.keys()) {
        if (!listed.has(id)) tiles.delete(id);
    }

    // Only touch the DOM when the list changed, so tiles (and open menus) aren't needlessly re-inserted.
    const children = feedList.children;
    const unchanged = children.length === elements.length && elements.every((el, i) => children[i] === el);
    if (!unchanged) feedList.replaceChildren(...elements);

    renderFeedEnd(state);
    updateSyncLabel();

    requestAnimationFrame(() => {
        measureClampedTiles();
        maybeLoadMore();
    });
}

/** The slot after the oldest packet: loading/retry/sign-in prompts, or the end of the feed. */
function renderFeedEnd(state: FeedState): void {
    feedEnd.className = "feed-end";
    feedEnd.replaceChildren();

    if (!state.loaded) {
        if (loadError) {
            feedEnd.classList.add("error");
            feedEnd.append(textEl("span", "", loadError), textButton("Retry", () => void refreshFeed(true)));
        } else if (signedIn) {
            feedEnd.append(spinner());
        } else {
            feedEnd.append(textEl("span", "", "Sign in to load your packets"));
        }
        return;
    }

    if (state.nextCursor) {
        if (state.loadMoreError && !state.loadingMore) {
            feedEnd.classList.add("error");
            feedEnd.append(
                textEl("span", "", "Couldn't load older packets."),
                textButton("Retry", () => void loadMore(true)),
            );
        } else if (!signedIn) {
            feedEnd.append(textEl("span", "", "Sign in to load older packets"));
        } else {
            feedEnd.append(spinner());
        }
        return;
    }

    if (state.packets.length === 0) {
        feedEnd.append(textEl("span", "", signedIn
            ? "No packets yet. Send one above, or from another device."
            : "Sign in to load your packets"));
        return;
    }

    feedEnd.append(textEl("span", "", "You've gone back to the beginning!"));
}

function createTile(packet: Packet): HTMLElement {
    const tile = document.createElement("article");
    tile.className = "packet";

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
        body.append(textEl("span", "packet-note", "File packets aren't supported yet."));
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

/** Adds a "Show more" button to newly shown clamped text that overflows, or unclamps text that fits. */
function measureClampedTiles(): void {
    for (const body of feedList.querySelectorAll<HTMLElement>(".packet-body.clamped:not([data-measured])")) {
        body.dataset.measured = "true";
        if (body.scrollHeight <= body.clientHeight + 1) {
            body.classList.remove("clamped");
            continue;
        }

        const more = textButton("Show more", () => {
            body.classList.remove("clamped");
            more.remove();
        });
        more.classList.add("show-more");
        body.after(more);
    }
}

function packetTitle(packet: Packet): string {
    return packet.title || (packet.type === "file" ? "File" : "Packet");
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

// ── Copy ──────────────────────────────────────────────────

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

    tile.append(menu);
    openMenu = menu;
    (menu.firstElementChild as HTMLElement).focus();
}

let openMenu: HTMLElement | null = null;

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

// ── Delete with undo ──────────────────────────────────────

/**
 * Removes the packet from the feed and shows an "Undo" toast. The service worker sends the deletion once
 * the undo window has passed, so it still happens if the popup is closed in the meantime.
 */
async function deleteWithUndo(packet: Packet): Promise<void> {
    if (!feed.hide(packet.id)) return;

    showToast("Packet deleted", {
        label: "Undo",
        onClick: async () => {
            const response = await sendMessage<CancelDeleteResponse>({action: "cancelDelete", id: packet.id});
            // Too late if it was already sent; the deletion's own response then removes it for good.
            if (response?.cancelled) feed.unhide(packet.id);
        },
    }, UNDO_DELETE_MS);

    const response = await sendMessage<DeletePacketResponse>({
        action: "deletePacket", id: packet.id, delayMs: UNDO_DELETE_MS,
    }).catch(() => null);

    if (response?.cancelled) return;
    if (response?.success) {
        feed.applyDeleted(packet.id);
        return;
    }

    feed.unhide(packet.id);
    if (response?.signedOut) setAuthState({signedIn: false});
    showToast(`Couldn't delete packet: ${response?.error || "please try again."}`);
}

// ── UI helpers ────────────────────────────────────────────

let statusTimer: ReturnType<typeof setTimeout> | undefined;

function showStatus(message: string, success: boolean): void {
    clearTimeout(statusTimer);
    statusEl.textContent = message;
    statusEl.className = `status-bar ${success ? "success" : "error"}`;
    statusTimer = setTimeout(() => {
        statusEl.className = "status-bar hidden";
    }, success ? 3000 : 6000);
}

let toastTimer: ReturnType<typeof setTimeout> | undefined;
let toastActionHandler: (() => void) | null = null;

/** Shows a snackbar-style toast, replacing any current one. */
function showToast(message: string, action?: { label: string; onClick: () => void }, durationMs = 3000): void {
    clearTimeout(toastTimer);
    toastMessage.textContent = message;
    toastAction.textContent = action?.label ?? "";
    toastAction.classList.toggle("hidden", !action);
    toastActionHandler = action?.onClick ?? null;
    toastEl.classList.remove("hidden");
    toastTimer = setTimeout(hideToast, durationMs);
}

function hideToast(): void {
    clearTimeout(toastTimer);
    toastEl.classList.add("hidden");
    toastActionHandler = null;
}

toastAction.addEventListener("click", () => {
    const handler = toastActionHandler;
    hideToast();
    handler?.();
});

function setRefreshing(value: boolean): void {
    refreshBtn.disabled = value;
    refreshBtn.classList.toggle("spinning", value);
}

function updateSyncLabel(): void {
    const text = live ? "Live" : syncLabel(feed.state.lastSyncedAt);
    syncLabelEl.textContent = text ?? "";
    syncLabelEl.classList.toggle("hidden", !text || !signedIn && !feed.state.loaded);
    syncLabelEl.classList.toggle("live", live);
}

// Keeps "Synced X ago" current while the popup stays open.
setInterval(updateSyncLabel, 60_000);

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

function isLoading(button: HTMLButtonElement): boolean {
    return button.classList.contains("loading");
}

function setLoading(button: HTMLButtonElement, loading: boolean): void {
    button.classList.toggle("loading", loading);
    button.disabled = loading;
    updateSendButtons();
}

function updateSendButtons(): void {
    sendPageBtn.disabled = !signedIn || isLoading(sendPageBtn);
    sendTextBtn.disabled = !signedIn || isLoading(sendTextBtn) || freetextEl.value.trim().length === 0;
}

function textEl(tag: string, className: string, text: string): HTMLElement {
    const el = document.createElement(tag);
    if (className) el.className = className;
    el.textContent = text;
    return el;
}

function textButton(label: string, onClick: () => void): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "text-btn";
    button.textContent = label;
    button.addEventListener("click", onClick);
    return button;
}

function iconButton(name: IconName, title: string, onClick: () => void): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "md-btn-icon";
    button.title = title;
    button.setAttribute("aria-label", title);
    button.append(icon(name));
    button.addEventListener("click", onClick);
    return button;
}

function spinner(): HTMLElement {
    return textEl("span", "spinner", "");
}

// ── Icons ─────────────────────────────────────────────────

const ICONS = {
    link: '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>',
    text: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="16" y1="13" x2="8" y2="13"/><line x1="16" y1="17" x2="8" y2="17"/>',
    file: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/>',
    image: '<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="9" cy="9" r="2"/><path d="m21 15-3.09-3.09a2 2 0 0 0-2.82 0L6 21"/>',
    external: '<path d="M15 3h6v6"/><path d="M10 14 21 3"/><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>',
    copy: '<rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>',
    trash: '<path d="M3 6h18"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>',
} as const;

type IconName = keyof typeof ICONS;

function icon(name: IconName): SVGSVGElement {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("aria-hidden", "true");
    svg.classList.add("icon");
    // Static markup from ICONS above; never user data.
    svg.innerHTML = ICONS[name];
    return svg;
}
