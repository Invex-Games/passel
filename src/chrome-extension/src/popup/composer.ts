import {packetDataForInput, truncateTitle} from "@passel/core";
import type {PacketData} from "@passel/core";
import {freetextEl, inSidePanel, isLoading, sendPageBtn, sendTextBtn} from "./dom.js";
import {showError} from "./toast.js";

// The composer: sending the current page, and sending typed text or a link. A paste anywhere in the popup
// lands in the text field.

/** The optional permission the side panel asks for, to read the address of whichever tab is showing. */
const TABS_PERMISSION: chrome.permissions.Permissions = {permissions: ["tabs"]};

const NO_TAB_IN_SIDE_PANEL_MESSAGE =
    "To send pages from the side panel, allow Passel to see your tabs, or send this one from the toolbar button.";

/**
 * The tab the user is looking at. The popup always opens from it, so Chrome's activeTab grant lets the popup
 * read its address. The side panel stays open as the user moves between tabs, so it first asks for the optional
 * tabs permission; Chrome answers at once, without asking again, once that's been granted. Declined, the
 * address is only there for a tab the user opened Passel from.
 */
async function currentTab(): Promise<chrome.tabs.Tab | undefined> {
    // Asked before anything else is awaited: Chrome only shows the request in response to the click.
    if (inSidePanel) await chrome.permissions.request(TABS_PERMISSION).catch(() => false);

    const [tab] = await chrome.tabs.query({active: true, currentWindow: true});
    return tab;
}

export interface ComposerDeps {
    isSignedIn(): boolean;
    /** Sends the packet through the controller, which updates the feed and the session; true on success. */
    send(data: PacketData, successMessage: string): Promise<boolean>;
}

export interface Composer {
    /** Enables the send buttons as the session and the text allow. */
    updateSendButtons(): void;
    /** Puts a button in or out of its loading state (spinner, disabled). */
    setLoading(button: HTMLButtonElement, loading: boolean): void;
}

export function createComposer(deps: ComposerDeps): Composer {
    function updateSendButtons(): void {
        const signedIn = deps.isSignedIn();
        sendPageBtn.disabled = !signedIn || isLoading(sendPageBtn);
        sendTextBtn.disabled = !signedIn || isLoading(sendTextBtn) || freetextEl.value.trim().length === 0;
    }

    function setLoading(button: HTMLButtonElement, loading: boolean): void {
        button.classList.toggle("loading", loading);
        button.disabled = loading;
        updateSendButtons();
    }

    // ── Send page ─────────────────────────────────────────

    sendPageBtn.addEventListener("click", async () => {
        setLoading(sendPageBtn, true);
        try {
            const tab = await currentTab();
            if (!tab?.url) {
                showError(inSidePanel ? NO_TAB_IN_SIDE_PANEL_MESSAGE : "Could not get current tab.");
                return;
            }
            await deps.send({$type: "Link", name: tab.title ? truncateTitle(tab.title) : null, url: tab.url}, "Page sent!");
        } catch (err) {
            showError(`Error: ${(err as Error).message}`);
        } finally {
            setLoading(sendPageBtn, false);
        }
    });

    // ── Send text or link ─────────────────────────────────

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
            if (await deps.send(data, data.$type === "Link" ? "Link sent!" : "Note sent!")) {
                freetextEl.value = "";
            }
        } catch (err) {
            showError(`Error: ${(err as Error).message}`);
        } finally {
            setLoading(sendTextBtn, false);
        }
    });

    // ── Clipboard paste ───────────────────────────────────

    document.addEventListener("paste", (e: ClipboardEvent) => {
        if (!deps.isSignedIn() || document.activeElement === freetextEl) return;

        const text = e.clipboardData?.getData("text/plain")?.trim();
        if (text) {
            e.preventDefault();
            freetextEl.value = text;
            updateSendButtons();
            freetextEl.focus();
        }
    });

    return {updateSendButtons, setLoading};
}
