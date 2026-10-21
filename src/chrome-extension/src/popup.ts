import type {
    AuthStateResponse,
    ExtensionMessage,
    PacketData,
    SendPacketResponse,
    SignInResponse,
    SignOutResponse,
} from "./types.js";

// All API calls and sign-in are delegated to the service worker, so they complete even if the
// popup closes (e.g. when the Google sign-in window takes focus).

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
const authRequiredSections = document.querySelectorAll<HTMLElement>(".auth-required");

let signedIn = false;

function sendMessage<R>(message: ExtensionMessage): Promise<R> {
    return chrome.runtime.sendMessage<ExtensionMessage, R>(message);
}

// ── Auth state ────────────────────────────────────────────

function setAuthState(state: AuthStateResponse): void {
    signedIn = state.signedIn;
    userEmailEl.textContent = state.email || "Signed in";

    authSignedOut.classList.toggle("hidden", signedIn);
    authDivider.classList.toggle("hidden", signedIn);
    authSignedIn.classList.toggle("hidden", !signedIn);
    authRequiredSections.forEach(s => s.classList.toggle("disabled-section", !signedIn));

    freetextEl.disabled = !signedIn;
    updateSendButtons();
}

sendMessage<AuthStateResponse>({action: "getAuthState"})
    .then(state => setAuthState(state ?? {signedIn: false}))
    .catch(() => setAuthState({signedIn: false}));

signInBtn.addEventListener("click", async () => {
    setLoading(signInBtn, true);
    try {
        const response = await sendMessage<SignInResponse>({action: "signIn"});
        if (response?.success) {
            setAuthState({signedIn: true, email: response.email});
            showStatus("Signed in!", true);
        } else {
            showStatus(response?.error || "Sign-in failed.", false);
        }
    } catch (err) {
        showStatus(`Error: ${(err as Error).message}`, false);
    } finally {
        setLoading(signInBtn, false);
    }
});

signOutBtn.addEventListener("click", async () => {
    await sendMessage<SignOutResponse>({action: "signOut"});
    setAuthState({signedIn: false});
    showStatus("Signed out.", true);
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
        await send({$type: "Link", name: tab.title || null, url: tab.url}, "Page sent!");
    } catch (err) {
        showStatus(`Error: ${(err as Error).message}`, false);
    } finally {
        setLoading(sendPageBtn, false);
    }
});

// ── Send text ─────────────────────────────────────────────

freetextEl.addEventListener("input", updateSendButtons);

sendTextBtn.addEventListener("click", async () => {
    const text = freetextEl.value.trim();
    if (!text) return;

    setLoading(sendTextBtn, true);
    try {
        if (await send({$type: "Text", title: null, body: text}, "Text sent!")) {
            freetextEl.value = "";
        }
    } catch (err) {
        showStatus(`Error: ${(err as Error).message}`, false);
    } finally {
        setLoading(sendTextBtn, false);
    }
});

async function send(data: PacketData, successMessage: string): Promise<boolean> {
    const response = await sendMessage<SendPacketResponse>({action: "sendPacket", data});
    if (response?.signedOut) setAuthState({signedIn: false});

    if (response?.success) {
        showStatus(successMessage, true);
        return true;
    }

    showStatus(response?.error || "Failed to send.", false);
    return false;
}

// ── Clipboard paste ───────────────────────────────────────

document.addEventListener("paste", (e: ClipboardEvent) => {
    if (!signedIn || document.activeElement === freetextEl) return;

    const text = e.clipboardData?.getData("text/plain")?.trim();
    if (text) {
        e.preventDefault();
        freetextEl.value = text;
        updateSendButtons();
        freetextEl.focus();
    }
});

// ── UI helpers ────────────────────────────────────────────

let statusTimer: ReturnType<typeof setTimeout> | undefined;

function showStatus(message: string, success: boolean): void {
    clearTimeout(statusTimer);
    statusEl.textContent = message;
    statusEl.className = `status-bar ${success ? "success" : "error"}`;
    statusTimer = setTimeout(() => {
        statusEl.className = "status-bar hidden";
    }, success ? 3000 : 6000);
}

function isLoading(button: HTMLButtonElement): boolean {
    return button.classList.contains("loading");
}

function setLoading(button: HTMLButtonElement, loading: boolean): void {
    button.classList.toggle("loading", loading);
    button.disabled = loading;
    updateSendButtons();
}

function updateSendButtons(): void {
    sendPageBtn.disabled = !signedIn || isLoading(sendPageBtn);
    sendTextBtn.disabled = !signedIn || isLoading(sendTextBtn) || freetextEl.value.trim().length === 0;
}
