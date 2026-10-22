import {packetDataForInput, truncateTitle} from "@passel/core";
import type {PacketData} from "@passel/core";
import {freetextEl, isLoading, sendPageBtn, sendTextBtn} from "./dom.js";
import {showError} from "./toast.js";

// The composer: sending the current page, and sending typed text or a link. A paste anywhere in the popup
// lands in the text field.

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
            const [tab] = await chrome.tabs.query({active: true, currentWindow: true});
            if (!tab?.url) {
                showError("Could not get current tab.");
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
