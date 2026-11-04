import {packetDataForInput} from "@passel/core";
import type {SendDeps} from "./commands.js";

// Only the service worker uses this module. The omnibox keyword is declared in static/manifest.json: typing
// "passel", a space and some text in the address bar offers to send it, and Enter sends it, as a link if it's a
// web URL and as a note otherwise, just like text typed into the popup.

/** How much of the typed text the suggestion repeats back. */
const PREVIEW_LENGTH = 80;

export const EMPTY_SUGGESTION = "Type a note or paste a link, then press Enter to send it to your devices";

export function registerOmnibox(deps: SendDeps): void {
    chrome.omnibox.onInputStarted.addListener(() => {
        void chrome.omnibox.setDefaultSuggestion({description: suggestionFor("")});
    });

    chrome.omnibox.onInputChanged.addListener((text, suggest) => {
        void chrome.omnibox.setDefaultSuggestion({description: suggestionFor(text)});
        suggest([]);
    });

    chrome.omnibox.onInputEntered.addListener(text => void sendInput(text, deps));
}

/** The default suggestion for what has been typed so far, in the omnibox's XML-style markup. */
export function suggestionFor(text: string): string {
    const trimmed = text.trim();
    if (!trimmed) return EMPTY_SUGGESTION;

    const data = packetDataForInput(trimmed);
    return data.$type === "Link"
        ? `Send link: <url>${escapeXml(preview(data.url))}</url> <dim>to your devices</dim>`
        : `Send note: <match>${escapeXml(preview(data.body))}</match> <dim>to your devices</dim>`;
}

export async function sendInput(text: string, deps: SendDeps): Promise<void> {
    const trimmed = text.trim();
    await (trimmed ? deps.send(packetDataForInput(trimmed)) : deps.fail());
}

function preview(text: string): string {
    // By code point, so an emoji isn't cut in half.
    const chars = [...text];
    return chars.length <= PREVIEW_LENGTH ? text : `${chars.slice(0, PREVIEW_LENGTH - 1).join("")}…`;
}

/** The description is parsed as XML, so typed text must not be able to open a tag (or break the parse). */
function escapeXml(text: string): string {
    return text.replace(/[&<>"']/g, c => `&${{"&": "amp", "<": "lt", ">": "gt", "\"": "quot", "'": "apos"}[c]};`);
}
