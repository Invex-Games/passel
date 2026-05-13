import {parseJwtPayload} from "./auth.js";
import {sendPacket, uploadParcel} from "./api.js";
import type {AuthStateResponse, SignInResponse} from "./types.js";

// ── DOM references ────────────────────────────────────────

const sendPageBtn = document.getElementById("send-page") as HTMLButtonElement;
const sendTextBtn = document.getElementById("send-text") as HTMLButtonElement;
const freetextEl = document.getElementById("freetext") as HTMLTextAreaElement;
const statusEl = document.getElementById("status") as HTMLDivElement;
const signInBtn = document.getElementById("sign-in-btn") as HTMLButtonElement;
const signOutBtn = document.getElementById("sign-out-btn") as HTMLButtonElement;
const authSignedOut = document.getElementById("auth-signed-out") as HTMLElement;
const authSignedIn = document.getElementById("auth-signed-in") as HTMLElement;
const authDivider = document.getElementById("auth-divider") as HTMLElement;
const userEmailEl = document.getElementById("user-email") as HTMLSpanElement;
const dropZone = document.getElementById("drop-zone") as HTMLDivElement;
const imageInput = document.getElementById("image-input") as HTMLInputElement;
const imagePreviewContainer = document.getElementById("image-preview-container") as HTMLDivElement;
const imagePreview = document.getElementById("image-preview") as HTMLImageElement;
const clearImageBtn = document.getElementById("clear-image") as HTMLButtonElement;
const sendImageBtn = document.getElementById("send-image") as HTMLButtonElement;

const MAX_IMAGE_SIZE = 5 * 1024 * 1024; // 5 MB
let pendingImageFile: File | null = null;

// ── Auth state ────────────────────────────────────────────

function setAuthState(token: string | null): void {
    if (token) {
        const claims = parseJwtPayload(token);
        userEmailEl.textContent = (claims.email as string) || (claims.name as string) || "Signed in";
        authSignedOut.classList.add("hidden");
        authDivider.classList.add("hidden");
        authSignedIn.classList.remove("hidden");

        sendPageBtn.disabled = false;
        freetextEl.disabled = false;
        imageInput.disabled = false;
        document.querySelectorAll(".disabled-section").forEach(s => s.classList.remove("disabled-section"));
    } else {
        authSignedOut.classList.remove("hidden");
        authDivider.classList.remove("hidden");
        authSignedIn.classList.add("hidden");

        sendPageBtn.disabled = true;
        sendTextBtn.disabled = true;
        freetextEl.disabled = true;
        sendImageBtn.disabled = true;
        imageInput.disabled = true;
        clearPendingImage();
    }
}

chrome.runtime.sendMessage({action: "getAuthState"}, (response?: AuthStateResponse) => {
    setAuthState(response?.token ?? null);
});

signInBtn.addEventListener("click", () => {
    setLoading(signInBtn, true);
    chrome.runtime.sendMessage({action: "signIn"}, (response?: SignInResponse) => {
        setLoading(signInBtn, false);
        if (response?.success && response.token) {
            setAuthState(response.token);
            showStatus("Signed in!", true);
        } else {
            showStatus(response?.error || "Sign-in failed.", false);
        }
    });
});

signOutBtn.addEventListener("click", () => {
    chrome.runtime.sendMessage({action: "signOut"}, () => {
        setAuthState(null);
        showStatus("Signed out.", true);
    });
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
        const ok = await sendPacket({$type: "Link", name: tab.title || null, url: tab.url});
        showStatus(ok ? "Page sent!" : "Failed to send page.", ok);
    } catch (err) {
        showStatus(`Error: ${(err as Error).message}`, false);
    } finally {
        setLoading(sendPageBtn, false);
    }
});

// ── Send text ─────────────────────────────────────────────

freetextEl.addEventListener("input", () => {
    sendTextBtn.disabled = freetextEl.value.trim().length === 0;
});

sendTextBtn.addEventListener("click", async () => {
    const text = freetextEl.value.trim();
    if (!text) return;

    setLoading(sendTextBtn, true);
    try {
        const ok = await sendPacket({$type: "Text", title: null, body: text});
        showStatus(ok ? "Text sent!" : "Failed to send text.", ok);
        if (ok) freetextEl.value = "";
        sendTextBtn.disabled = true;
    } catch (err) {
        showStatus(`Error: ${(err as Error).message}`, false);
    } finally {
        setLoading(sendTextBtn, false);
    }
});

// ── Image handling ────────────────────────────────────────

dropZone.addEventListener("click", () => imageInput.click());

imageInput.addEventListener("change", () => {
    if (imageInput.files?.[0]) loadImageFile(imageInput.files[0]);
});

clearImageBtn.addEventListener("click", clearPendingImage);

dropZone.addEventListener("dragover", (e) => {
    e.preventDefault();
    dropZone.classList.add("drop-zone-active");
});

dropZone.addEventListener("dragleave", () => {
    dropZone.classList.remove("drop-zone-active");
});

dropZone.addEventListener("drop", (e) => {
    e.preventDefault();
    dropZone.classList.remove("drop-zone-active");
    const file = e.dataTransfer?.files[0];
    if (file) loadImageFile(file);
});

sendImageBtn.addEventListener("click", async () => {
    if (!pendingImageFile) return;

    setLoading(sendImageBtn, true);
    try {
        const parcelId = await uploadParcel(pendingImageFile, pendingImageFile.name);
        const ok = await sendPacket({
            $type: "File",
            fileName: pendingImageFile.name || null,
            contentType: pendingImageFile.type,
            payload: parcelId,
        });
        showStatus(ok ? "Image sent!" : "Failed to send image.", ok);
        if (ok) clearPendingImage();
    } catch (err) {
        showStatus(`Error: ${(err as Error).message}`, false);
    } finally {
        setLoading(sendImageBtn, false);
    }
});

function loadImageFile(file: File): void {
    if (!file.type.startsWith("image/")) {
        showStatus("Please select an image file.", false);
        return;
    }
    if (file.size > MAX_IMAGE_SIZE) {
        showStatus("Image must be under 5 MB.", false);
        return;
    }

    pendingImageFile = file;
    const reader = new FileReader();
    reader.onload = (e) => {
        imagePreview.src = e.target!.result as string;
        imagePreviewContainer.classList.remove("hidden");
        dropZone.classList.add("hidden");
        sendImageBtn.disabled = false;
    };
    reader.onerror = () => showStatus("Failed to read image.", false);
    reader.readAsDataURL(file);
}

function clearPendingImage(): void {
    pendingImageFile = null;
    imagePreview.src = "";
    imagePreviewContainer.classList.add("hidden");
    dropZone.classList.remove("hidden");
    sendImageBtn.disabled = true;
    imageInput.value = "";
}

// ── Clipboard paste ───────────────────────────────────────

document.addEventListener("paste", (e: ClipboardEvent) => {
    if (document.activeElement === freetextEl) return;

    const items = e.clipboardData?.items;
    if (!items) return;

    for (const item of items) {
        if (item.type.startsWith("image/")) {
            e.preventDefault();
            const file = item.getAsFile();
            if (file) loadImageFile(file);
            return;
        }
    }

    const text = e.clipboardData?.getData("text/plain")?.trim();
    if (text) {
        e.preventDefault();
        freetextEl.value = text;
        freetextEl.dispatchEvent(new Event("input"));
        freetextEl.focus();
    }
});

// ── UI helpers ────────────────────────────────────────────

function showStatus(message: string, success: boolean): void {
    statusEl.textContent = message;
    statusEl.className = `status-bar ${success ? "success" : "error"}`;
    setTimeout(() => {
        statusEl.className = "status-bar hidden";
    }, 3000);
}

function setLoading(button: HTMLButtonElement, loading: boolean): void {
    if (loading) {
        button.disabled = true;
        button.dataset.originalHtml = button.innerHTML;
        button.innerHTML = `<span class="spinner"></span>Sending…`;
    } else {
        button.innerHTML = button.dataset.originalHtml || "";
        if (button !== sendTextBtn || freetextEl.value.trim().length > 0) {
            button.disabled = false;
        }
    }
}
