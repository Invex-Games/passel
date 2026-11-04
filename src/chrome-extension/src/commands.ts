import {truncateTitle} from "@passel/core";
import type {PacketData} from "@passel/core";

// Only the service worker uses this module. The keyboard commands are declared in static/manifest.json, with
// suggested keys the user can change at chrome://extensions/shortcuts. Chrome handles _execute_action (opening
// the popup) itself; the two below arrive here.

export const SEND_PAGE_COMMAND = "send-current-page";
export const OPEN_SIDE_PANEL_COMMAND = "open-side-panel";

/** How the service worker sends, and reports the outcome on the toolbar badge. */
export interface SendDeps {
    send(data: PacketData): Promise<unknown>;
    /** Shows a failure on the badge, for a user action that had nothing to send. */
    fail(): Promise<void>;
}

export function registerCommands(deps: SendDeps): void {
    chrome.commands.onCommand.addListener((command, tab) => void handleCommand(command, tab, deps));
}

export async function handleCommand(command: string, tab: chrome.tabs.Tab | undefined, deps: SendDeps): Promise<void> {
    switch (command) {
        case OPEN_SIDE_PANEL_COMMAND:
            // Opened before anything is awaited: Chrome only opens the side panel in response to a user action.
            if (tab?.windowId === undefined) return;
            await chrome.sidePanel.open({windowId: tab.windowId}).catch(err => {
                console.warn("Passel: couldn't open the side panel", err);
            });
            return;

        case SEND_PAGE_COMMAND: {
            const data = tab ? await packetFromTab(tab) : null;
            await (data ? deps.send(data) : deps.fail());
            return;
        }
    }
}

/**
 * What the send command sends from a tab: the selected text as a note if there is any, otherwise the page as a
 * link. The command grants activeTab on the tab, which is what lets the worker read both.
 */
export async function packetFromTab(tab: chrome.tabs.Tab): Promise<PacketData | null> {
    const selection = tab.id !== undefined ? await selectionIn(tab.id) : null;
    if (selection) return {$type: "Text", title: null, body: selection};

    return tab.url ? {$type: "Link", name: tab.title ? truncateTitle(tab.title) : null, url: tab.url} : null;
}

async function selectionIn(tabId: number): Promise<string | null> {
    try {
        const [frame] = await chrome.scripting.executeScript({
            target: {tabId},
            func: () => getSelection()?.toString() ?? "",
        });
        const text = typeof frame?.result === "string" ? frame.result.trim() : "";
        return text || null;
    } catch {
        // Pages no extension may script (chrome:// pages, the Web Store) can't be read; the page goes as a link.
        return null;
    }
}
