import {readFileSync} from "node:fs";
import {dirname, join} from "node:path";
import {fileURLToPath} from "node:url";
import {vi} from "vitest";

// Loads static/popup.html into jsdom's document, as the popup or as the side panel (the same page with a
// `side-panel` class on the body, as build.mjs writes it), and imports the popup's modules fresh: they look up
// their elements, and whether they're in the side panel, as they load.

// By path rather than by URL: under jsdom, URL is jsdom's own class, which fs doesn't take.
const POPUP_HTML = readFileSync(join(dirname(fileURLToPath(import.meta.url)), "..", "static", "popup.html"), "utf8");

export async function loadPopupPage({sidePanel = false} = {}) {
    const page = new DOMParser().parseFromString(POPUP_HTML, "text/html");
    document.head.replaceWith(page.head);
    document.body.replaceWith(page.body);
    if (sidePanel) document.body.classList.add("side-panel");

    vi.resetModules();
    return {
        dom: await import("../src/popup/dom.js"),
        composer: await import("../src/popup/composer.js"),
        controller: await import("../src/popup/controller.js"),
    };
}

/** The snackbar's text, where every piece of the popup's feedback goes. */
export function toastText(): string {
    return document.getElementById("toast-message")?.textContent ?? "";
}

export function type(field: HTMLTextAreaElement | HTMLInputElement, text: string): void {
    field.value = text;
    field.dispatchEvent(new Event("input", {bubbles: true}));
}

export function press(target: Element, key: string, init: KeyboardEventInit = {}): KeyboardEvent {
    const event = new KeyboardEvent("keydown", {key, bubbles: true, cancelable: true, ...init});
    target.dispatchEvent(event);
    return event;
}
