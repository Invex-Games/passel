import type {ExtensionMessage} from "@passel/core";

// The popup's fixed elements (declared in static/popup.html), the message channel to the service worker,
// and the small element builders the views share.

export const mainEl = document.getElementById("main") as HTMLElement;
export const sendPageBtn = document.getElementById("send-page") as HTMLButtonElement;
export const sendTextBtn = document.getElementById("send-text") as HTMLButtonElement;
export const freetextEl = document.getElementById("freetext") as HTMLTextAreaElement;
export const signInBtn = document.getElementById("sign-in-btn") as HTMLButtonElement;
export const devSignInEl = document.getElementById("dev-sign-in") as HTMLElement;
export const devUserEl = document.getElementById("dev-user") as HTMLInputElement;
export const devSignInBtn = document.getElementById("dev-sign-in-btn") as HTMLButtonElement;
export const signOutBtn = document.getElementById("sign-out-btn") as HTMLButtonElement;
export const refreshBtn = document.getElementById("refresh-btn") as HTMLButtonElement;
export const syncLabelEl = document.getElementById("sync-label") as HTMLSpanElement;
export const authSignedOut = document.getElementById("auth-signed-out") as HTMLElement;
export const authSignedIn = document.getElementById("auth-signed-in") as HTMLElement;
export const userEmailEl = document.getElementById("user-email") as HTMLSpanElement;
export const composerEl = document.getElementById("composer") as HTMLElement;
export const feedSection = document.querySelector(".feed") as HTMLElement;
export const feedList = document.getElementById("feed-list") as HTMLDivElement;
export const feedEnd = document.getElementById("feed-end") as HTMLDivElement;
export const toastEl = document.getElementById("toast") as HTMLDivElement;
export const toastMessage = document.getElementById("toast-message") as HTMLSpanElement;
export const toastAction = document.getElementById("toast-action") as HTMLButtonElement;

/** Sends a message to the service worker, which does all API calls and sign-in so they outlive the popup. */
export function sendMessage<R>(message: ExtensionMessage): Promise<R> {
    return chrome.runtime.sendMessage<ExtensionMessage, R>(message);
}

export function isLoading(button: HTMLButtonElement): boolean {
    return button.classList.contains("loading");
}

export function textEl(tag: string, className: string, text: string): HTMLElement {
    const el = document.createElement(tag);
    if (className) el.className = className;
    el.textContent = text;
    return el;
}

export function textButton(label: string, onClick: () => void): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "text-btn";
    button.textContent = label;
    button.addEventListener("click", onClick);
    return button;
}

export function iconButton(name: IconName, title: string, onClick: () => void): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "md-btn-icon";
    button.title = title;
    button.setAttribute("aria-label", title);
    button.append(icon(name));
    button.addEventListener("click", onClick);
    return button;
}

export function spinner(): HTMLElement {
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

export type IconName = keyof typeof ICONS;

export function icon(name: IconName): SVGSVGElement {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("aria-hidden", "true");
    svg.classList.add("icon");
    // Static markup from ICONS above; never user data.
    svg.innerHTML = ICONS[name];
    return svg;
}
