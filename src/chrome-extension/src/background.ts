import {getIdToken, removeIdToken, signIn} from "./auth.js";
import {sendPacket, uploadParcel} from "./api.js";
import type {ExtensionMessage} from "./types.js";

// ── Context menus ─────────────────────────────────────────

chrome.runtime.onInstalled.addListener(() => {
    chrome.contextMenus.create({id: "passel-link", title: "Send link to Passel", contexts: ["link"]});
    chrome.contextMenus.create({id: "passel-selection", title: "Send selection to Passel", contexts: ["selection"]});
    chrome.contextMenus.create({id: "passel-image", title: "Send image to Passel", contexts: ["image"]});
});

chrome.contextMenus.onClicked.addListener(async (info) => {
    try {
        switch (info.menuItemId) {
            case "passel-link":
                await sendPacket({$type: "Link", name: info.selectionText || null, url: info.linkUrl!});
                break;

            case "passel-selection":
                await sendPacket({$type: "Text", title: null, body: info.selectionText!});
                break;

            case "passel-image":
                await handleImageContext(info.srcUrl!);
                break;
        }
    } catch (error) {
        console.error("Passel: context menu action failed", error);
    }
});

async function handleImageContext(srcUrl: string): Promise<void> {
    try {
        const imgResponse = await fetch(srcUrl);
        if (imgResponse.ok) {
            const blob = await imgResponse.blob();
            const fileName = extractFilename(srcUrl, blob.type);
            const parcelId = await uploadParcel(blob, fileName);
            await sendPacket({$type: "File", fileName, contentType: blob.type, payload: parcelId});
            return;
        }
    } catch (err) {
        console.warn("Passel: could not upload image, falling back to URL", err);
    }

    // File packets aren't supported by the server yet, so this is the usual path for now.
    await sendPacket({$type: "Link", name: extractFilename(srcUrl), url: srcUrl});
}

function extractFilename(url: string, mimeType?: string): string {
    try {
        const basename = new URL(url).pathname.split("/").pop();
        if (basename?.includes(".")) return basename;
    } catch { /* ignore invalid URLs */
    }

    const ext = mimeType?.split("/")[1]?.replace("jpeg", "jpg") || "png";
    return `image.${ext}`;
}

// ── Message handler (from popup) ──────────────────────────

chrome.runtime.onMessage.addListener((message: ExtensionMessage, _sender, sendResponse) => {
    switch (message.action) {
        case "signIn":
            signIn()
                .then(token => sendResponse({success: true, token}))
                .catch(err => sendResponse({success: false, error: (err as Error).message}));
            return true;

        case "signOut":
            removeIdToken().then(() => sendResponse({success: true}));
            return true;

        case "getAuthState":
            getIdToken().then(token => sendResponse({token}));
            return true;
    }
});
