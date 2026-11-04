import {FEED_PORT_NAME} from "@passel/core";
import type {
    AuthStateResponse,
    FeedPortMessage,
    PacketData,
    SendPacketResponse,
    SignInMessage,
    SignInResponse,
    SignOutResponse,
} from "@passel/core";
import {DEV_SIGN_IN, GOOGLE_CLIENT_ID} from "../config.js";
import {createComposer} from "./composer.js";
import {
    authSignedIn,
    authSignedOut,
    composerEl,
    devSignInBtn,
    devSignInEl,
    devUserEl,
    feedSection,
    freetextEl,
    inSidePanel,
    refreshBtn,
    sendMessage,
    sidePanelBtn,
    signInBtn,
    signOutBtn,
    userEmailEl,
} from "./dom.js";
import {createFeedView} from "./feed-view.js";
import {showError, showSuccess} from "./toast.js";

// Owns the popup's session: who is signed in, the live connection to the service worker, and the hand-offs
// between the feed view, the composer and the toast. All API calls and sign-in are delegated to the service
// worker, so they complete even if the popup closes (e.g. when the Google sign-in window takes focus).

/** How often the popup pings the service worker so it stays alive to forward live updates. */
const KEEP_ALIVE_INTERVAL_MS = 20_000;

/**
 * A resync after the stream reconnects waits up to this long before refetching, so popups that all lost the
 * stream at the same moment (a server restart) don't all hit the API at once.
 */
const MAX_RESYNC_DELAY_MS = 3000;

export function startPopup(): void {
    let signedIn = false;
    let live = false;

    const feedView = createFeedView({
        isSignedIn: () => signedIn,
        isLive: () => live,
        onSignedOut: () => setAuthState({signedIn: false}),
    });

    const composer = createComposer({
        isSignedIn: () => signedIn,
        send,
    });

    // A development build with DEV_SIGN_IN can sign in as a development user; built without a Google client
    // id (only possible then), that is the only way to sign in.
    devSignInEl.classList.toggle("hidden", !DEV_SIGN_IN);
    signInBtn.classList.toggle("hidden", !GOOGLE_CLIENT_ID);

    // ── Live updates ──────────────────────────────────────

    let port: chrome.runtime.Port | null = null;
    let keepAliveTimer: ReturnType<typeof setInterval> | undefined;
    let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
    let resyncTimer: ReturnType<typeof setTimeout> | undefined;

    /** Whether a resync has happened since the popup opened: the first one is immediate, later ones are spread out. */
    let resynced = false;

    function connectLive(): void {
        if (port) return;
        clearTimeout(reconnectTimer);

        const connected = chrome.runtime.connect({name: FEED_PORT_NAME});
        port = connected;
        connected.onMessage.addListener(onPortMessage);
        connected.onDisconnect.addListener(() => {
            // The service worker restarted; reconnect so live updates resume.
            if (port !== connected) return;
            port = null;
            clearInterval(keepAliveTimer);
            setLive(false);
            if (signedIn) reconnectTimer = setTimeout(connectLive, 1000);
        });
        keepAliveTimer = setInterval(() => port?.postMessage({type: "ping"}), KEEP_ALIVE_INTERVAL_MS);
    }

    function disconnectLive(): void {
        clearTimeout(reconnectTimer);
        clearTimeout(resyncTimer);
        clearInterval(keepAliveTimer);
        const connected = port;
        port = null;
        connected?.disconnect();
        setLive(false);
    }

    function onPortMessage(message: FeedPortMessage): void {
        switch (message.type) {
            case "status":
                setLive(message.live);
                break;

            case "signedOut":
                setAuthState({signedIn: false});
                break;

            case "event":
                switch (message.event.kind) {
                    case "resync":
                        scheduleResync();
                        break;
                    case "created":
                        feedView.applyArrived(message.event.packet);
                        break;
                    case "deleted":
                        feedView.applyDeleted(message.event.id);
                        break;
                }
                break;
        }
    }

    /**
     * Catches up on anything created or deleted while the stream was down. The first resync after the popup
     * opens runs at once, so the feed is fresh when the user looks at it; later ones (reconnects) are spread out.
     */
    function scheduleResync(): void {
        clearTimeout(resyncTimer);
        if (!resynced) {
            resynced = true;
            feedView.syncLatest();
            return;
        }
        resyncTimer = setTimeout(() => feedView.syncLatest(), Math.random() * MAX_RESYNC_DELAY_MS);
    }

    function setLive(value: boolean): void {
        live = value;
        feedView.updateSyncLabel();
    }

    // ── Session ───────────────────────────────────────────

    function setAuthState(state: AuthStateResponse): void {
        const wasSignedIn = signedIn;
        signedIn = state.signedIn;
        userEmailEl.textContent = state.email || "Signed in";

        authSignedOut.classList.toggle("hidden", signedIn);
        authSignedIn.classList.toggle("hidden", !signedIn);
        refreshBtn.classList.toggle("hidden", !signedIn);
        // Until now the composer was shown disabled (see popup.html); signed out, it isn't shown at all.
        composerEl.classList.remove("disabled-section");
        composerEl.classList.toggle("hidden", !signedIn);
        feedSection.classList.toggle("signed-out", !signedIn);

        freetextEl.disabled = !signedIn;
        composer.updateSendButtons();

        // Offer the development user last signed in as, unless the user has started typing another.
        if (!signedIn && state.lastDevUser && !devUserEl.value) devUserEl.value = state.lastDevUser;

        if (signedIn && !wasSignedIn) {
            connectLive();
            void feedView.refresh(false);
        } else if (!signedIn && wasSignedIn) {
            disconnectLive();
        }
        feedView.render();
    }

    async function send(data: PacketData, successMessage: string): Promise<boolean> {
        const response = await sendMessage<SendPacketResponse>({action: "sendPacket", data});
        if (response?.signedOut) setAuthState({signedIn: false});

        if (response?.success) {
            if (response.packet) feedView.applyArrived(response.packet);
            showSuccess(successMessage);
            return true;
        }

        showError(response?.error || "Failed to send.");
        return false;
    }

    async function runSignIn(button: HTMLButtonElement, message: SignInMessage): Promise<void> {
        composer.setLoading(button, true);
        try {
            const response = await sendMessage<SignInResponse>(message);
            if (response?.success) {
                // The account may have changed (the picker, or another development user), so don't keep
                // showing the previous feed.
                await feedView.clear();
                setAuthState({signedIn: true, email: response.email});
                showSuccess("Signed in!");
            } else {
                showError(response?.error || "Sign-in failed.");
            }
        } catch (err) {
            showError(`Error: ${(err as Error).message}`);
        } finally {
            composer.setLoading(button, false);
        }
    }

    signInBtn.addEventListener("click", () => void runSignIn(signInBtn, {action: "signIn"}));

    devSignInBtn.addEventListener("click", () => void runSignIn(devSignInBtn, {action: "signIn", devUser: devUserEl.value}));

    devUserEl.addEventListener("keydown", e => {
        if (e.key === "Enter" && !e.isComposing) {
            e.preventDefault();
            devSignInBtn.click();
        }
    });

    signOutBtn.addEventListener("click", async () => {
        await sendMessage<SignOutResponse>({action: "signOut"});
        setAuthState({signedIn: false});
        await feedView.clear();
        showSuccess("Signed out.");
    });

    // ── Side panel ────────────────────────────────────────

    // The popup can move into the side panel, which stays open beside the page. The window is looked up now
    // so the click can open the panel straight away: Chrome only opens it in response to a user action.
    let windowId: number | undefined;
    if (!inSidePanel) {
        chrome.windows.getCurrent().then(window => {
            windowId = window.id;
        }, () => {
        });
    }

    sidePanelBtn.addEventListener("click", () => {
        if (windowId === undefined) return;
        chrome.sidePanel.open({windowId}).then(
            () => window.close(),
            err => showError(`Couldn't open the side panel: ${(err as Error).message}`),
        );
    });

    void (async () => {
        // Show the cached feed straight away; checking the session can take a moment.
        await feedView.showCached();
        const state = await sendMessage<AuthStateResponse>({action: "getAuthState"}).catch(() => null);
        setAuthState(state ?? {signedIn: false});
    })();
}
