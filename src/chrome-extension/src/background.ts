import {getAccountName, getToken, signIn, signOut} from "./auth.js";
import {deletePacket, getPackets, sendPacket} from "./api.js";
import {addFeedPort} from "./live.js";
import {FEED_PORT_NAME} from "./types.js";
import type {
    AuthStateResponse,
    CancelDeleteResponse,
    DeletePacketResponse,
    ExtensionMessage,
    PacketData,
    SendPacketResponse,
} from "./types.js";

// ── Context menus ─────────────────────────────────────────

chrome.runtime.onInstalled.addListener(async () => {
    await chrome.contextMenus.removeAll();
    chrome.contextMenus.create({id: "passel-link", title: "Send link to Passel", contexts: ["link"]});
    chrome.contextMenus.create({id: "passel-selection", title: "Send selection to Passel", contexts: ["selection"]});
    chrome.contextMenus.create({id: "passel-image", title: "Send image link to Passel", contexts: ["image"]});

    // Earlier versions kept the token in local storage; it now lives in session storage.
    await chrome.storage.local.remove("idToken");
});

chrome.contextMenus.onClicked.addListener(async (info) => {
    const data = packetFromContext(info);
    if (!data) {
        await flashBadge(false);
        return;
    }

    // A context-menu click is user-initiated, so a sign-in window may be shown if needed.
    await sendAndReport(data, true);
});

function packetFromContext(info: chrome.contextMenus.OnClickData): PacketData | null {
    switch (info.menuItemId) {
        case "passel-link":
            return info.linkUrl ? {$type: "Link", name: info.selectionText || null, url: info.linkUrl} : null;

        case "passel-selection":
            return info.selectionText ? {$type: "Text", title: null, body: info.selectionText} : null;

        case "passel-image":
            // File packets aren't supported yet, so send the image's URL as a link.
            // data:/blob: URLs only exist in the source page, so they can't be sent.
            return info.srcUrl && /^https?:/i.test(info.srcUrl)
                ? {$type: "Link", name: fileNameFromUrl(info.srcUrl), url: info.srcUrl}
                : null;

        default:
            return null;
    }
}

function fileNameFromUrl(url: string): string | null {
    try {
        const basename = new URL(url).pathname.split("/").pop();
        return basename?.includes(".") ? decodeURIComponent(basename) : null;
    } catch {
        return null;
    }
}

// ── Sending ───────────────────────────────────────────────

async function sendAndReport(data: PacketData, interactive: boolean): Promise<SendPacketResponse> {
    const result = await sendPacket(data, interactive);
    await flashBadge(result.success);
    return result;
}

// ── Badge feedback ────────────────────────────────────────

const BADGE_DURATION_MS = 4000;
let badgeTimer: ReturnType<typeof setTimeout> | undefined;

async function flashBadge(success: boolean): Promise<void> {
    clearTimeout(badgeTimer);
    await chrome.action.setBadgeBackgroundColor({color: success ? "#2E7D32" : "#BA1A1A"});
    await chrome.action.setBadgeText({text: success ? "✓" : "!"});
    badgeTimer = setTimeout(() => chrome.action.setBadgeText({text: ""}), BADGE_DURATION_MS);
}

// ── Message handler (from popup) ──────────────────────────

chrome.runtime.onMessage.addListener((message: ExtensionMessage, sender, sendResponse) => {
    if (sender.id !== chrome.runtime.id) return;

    switch (message.action) {
        case "getAuthState":
            getAuthState().then(sendResponse);
            return true;

        case "signIn":
            signIn()
                .then(token => sendResponse({success: true, email: getAccountName(token)}))
                .catch(err => sendResponse({success: false, error: (err as Error).message}));
            return true;

        case "signOut":
            signOut().then(() => sendResponse({success: true}));
            return true;

        case "sendPacket":
            // Sending from the popup is user-initiated, so a sign-in window may be shown if needed.
            sendAndReport(message.data, true).then(sendResponse);
            return true;

        case "getPackets":
            getPackets(message.cursor).then(sendResponse);
            return true;

        case "deletePacket":
            scheduleDelete(message.id, message.delayMs).then(sendResponse);
            return true;

        case "cancelDelete":
            sendResponse({cancelled: cancelDelete(message.id)} satisfies CancelDeleteResponse);
            return false;
    }
});

// ── Live updates (to the popup's feed) ────────────────────

chrome.runtime.onConnect.addListener(port => {
    if (port.sender?.id !== chrome.runtime.id || port.name !== FEED_PORT_NAME) return;
    addFeedPort(port);
});

// ── Deleting with undo ────────────────────────────────────

// Deletions wait out the popup's undo window here rather than in the popup, so one still goes
// through if the popup closes before the window ends.
const pendingDeletes = new Map<string, {
    timer: ReturnType<typeof setTimeout>;
    resolve: (response: DeletePacketResponse) => void
}>();

function scheduleDelete(id: string, delayMs: number): Promise<DeletePacketResponse> {
    cancelDelete(id);

    return new Promise(resolve => {
        const timer = setTimeout(() => {
            pendingDeletes.delete(id);
            // Deleting is user-initiated, so a sign-in window may be shown if needed.
            deletePacket(id, true).then(resolve);
        }, delayMs);
        pendingDeletes.set(id, {timer, resolve});
    });
}

/** Returns false if the deletion was already sent (or never scheduled). */
function cancelDelete(id: string): boolean {
    const pending = pendingDeletes.get(id);
    if (!pending) return false;

    clearTimeout(pending.timer);
    pendingDeletes.delete(id);
    pending.resolve({success: false, cancelled: true});
    return true;
}

async function getAuthState(): Promise<AuthStateResponse> {
    // Not user-initiated: refresh silently if possible, but never show a sign-in window.
    const token = await getToken(false);
    return token ? {signedIn: true, email: getAccountName(token)} : {signedIn: false};
}
