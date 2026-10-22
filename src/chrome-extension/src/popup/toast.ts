import {toastAction, toastEl, toastMessage} from "./dom.js";

// All of the popup's feedback goes through one snackbar-style toast: it overlays the bottom of the popup,
// so nothing shifts the layout.

let toastTimer: ReturnType<typeof setTimeout> | undefined;
let toastActionHandler: (() => void) | null = null;

export interface ToastOptions {
    /** Colours the toast as a success or an error; by default it's the neutral snackbar. */
    tone?: "success" | "error";
    action?: { label: string; onClick: () => void };
    /** How long the toast stays. Errors stay longer by default, so there's time to read them. */
    durationMs?: number;
}

/** Shows a toast, replacing any current one. */
export function showToast(message: string, {tone, action, durationMs = tone === "error" ? 6000 : 3000}: ToastOptions = {}): void {
    clearTimeout(toastTimer);
    toastMessage.textContent = message;
    toastAction.textContent = action?.label ?? "";
    toastAction.classList.toggle("hidden", !action);
    toastActionHandler = action?.onClick ?? null;
    toastEl.className = tone ? `toast ${tone}` : "toast";
    // An error interrupts a screen reader; other feedback waits its turn.
    toastEl.setAttribute("aria-live", tone === "error" ? "assertive" : "polite");
    toastTimer = setTimeout(hideToast, durationMs);
}

export function showSuccess(message: string): void {
    showToast(message, {tone: "success"});
}

export function showError(message: string): void {
    showToast(message, {tone: "error"});
}

export function hideToast(): void {
    clearTimeout(toastTimer);
    toastEl.classList.add("hidden");
    toastActionHandler = null;
}

toastAction.addEventListener("click", () => {
    const handler = toastActionHandler;
    hideToast();
    handler?.();
});
