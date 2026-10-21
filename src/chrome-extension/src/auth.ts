import {GOOGLE_CLIENT_ID} from "./config.js";

// Only the service worker uses this module. The ID token lives in session storage (memory only,
// cleared when the browser closes); the login hint lives in local storage so a signed-in user can
// be re-authenticated silently after a restart. A missing hint means the user is signed out.
const TOKEN_KEY = "idToken";
const LOGIN_HINT_KEY = "loginHint";

// Tokens with less than this much lifetime left are treated as expired.
const EXPIRY_MARGIN_SECONDS = 5 * 60;

export interface JwtClaims {
    sub?: string;
    email?: string;
    name?: string;
    aud?: string | string[];
    exp?: number;
    nonce?: string;
}

// silent: no UI (prompt=none); reauth: interactive without forcing the account picker;
// selectAccount: explicit sign-in, always shows the account picker.
type AuthMode = "silent" | "reauth" | "selectAccount";

let pendingAuth: Promise<string> | null = null;

// ── Token storage ─────────────────────────────────────────

/** Returns the stored token if it isn't (nearly) expired. Never prompts. */
export async function getStoredToken(): Promise<string | null> {
    const result = await chrome.storage.session.get(TOKEN_KEY);
    const token = result[TOKEN_KEY] as string | undefined;
    return token && !isExpired(token) ? token : null;
}

/** Returns a usable token, refreshing it if needed. Only shows UI when `interactive` is true. */
export async function getToken(interactive: boolean): Promise<string | null> {
    return (await getStoredToken()) ?? refreshToken(interactive);
}

/**
 * Discards the current token and obtains a new one: silently if the user was signed in,
 * then interactively (without forcing the account picker) if `interactive` is true.
 */
export async function refreshToken(interactive: boolean): Promise<string | null> {
    await chrome.storage.session.remove(TOKEN_KEY);

    if (await getLoginHint()) {
        try {
            return await authorize("silent");
        } catch (err) {
            console.warn("Passel: silent sign-in failed", err);
        }
    }

    if (!interactive) return null;

    try {
        return await authorize("reauth");
    } catch (err) {
        console.error("Passel: sign-in failed", err);
        return null;
    }
}

/** Explicit, user-initiated sign-in. Always shows the Google account picker. */
export async function signIn(): Promise<string> {
    return authorize("selectAccount");
}

export async function signOut(): Promise<void> {
    await chrome.storage.session.remove(TOKEN_KEY);
    await chrome.storage.local.remove(LOGIN_HINT_KEY);
}

async function getLoginHint(): Promise<string | null> {
    const result = await chrome.storage.local.get(LOGIN_HINT_KEY);
    return (result[LOGIN_HINT_KEY] as string | undefined) || null;
}

// ── OAuth flow ────────────────────────────────────────────

function authorize(mode: AuthMode): Promise<string> {
    // Only one auth flow can be open at a time; concurrent callers share it.
    pendingAuth ??= runAuthFlow(mode).finally(() => {
        pendingAuth = null;
    });
    return pendingAuth;
}

async function runAuthFlow(mode: AuthMode): Promise<string> {
    const redirectUri = chrome.identity.getRedirectURL();
    const nonce = crypto.randomUUID();
    const state = crypto.randomUUID();
    const loginHint = await getLoginHint();

    const authUrl = new URL("https://accounts.google.com/o/oauth2/v2/auth");
    authUrl.searchParams.set("client_id", GOOGLE_CLIENT_ID);
    authUrl.searchParams.set("response_type", "id_token");
    authUrl.searchParams.set("redirect_uri", redirectUri);
    authUrl.searchParams.set("scope", "openid email profile");
    authUrl.searchParams.set("nonce", nonce);
    authUrl.searchParams.set("state", state);

    if (mode === "silent") authUrl.searchParams.set("prompt", "none");
    if (mode === "selectAccount") authUrl.searchParams.set("prompt", "select_account");
    if (loginHint && mode !== "selectAccount") authUrl.searchParams.set("login_hint", loginHint);

    const responseUrl = await chrome.identity.launchWebAuthFlow({
        url: authUrl.toString(), interactive: mode !== "silent",
    });

    if (!responseUrl) throw new Error("No response from auth flow");

    const params = new URLSearchParams(new URL(responseUrl).hash.substring(1));

    const error = params.get("error");
    if (error) throw new Error(params.get("error_description") || error);

    if (params.get("state") !== state) throw new Error("Sign-in response state mismatch");

    const idToken = params.get("id_token");
    if (!idToken) throw new Error("No ID token received");

    const claims = parseJwtPayload(idToken);
    if (!claims) throw new Error("Malformed ID token");
    if (claims.nonce !== nonce) throw new Error("ID token nonce mismatch");
    if (!audienceMatches(claims.aud)) throw new Error("ID token audience mismatch");
    if (isExpired(idToken)) throw new Error("ID token is expired");

    await chrome.storage.session.set({[TOKEN_KEY]: idToken});
    await chrome.storage.local.set({[LOGIN_HINT_KEY]: claims.email || claims.sub || ""});
    return idToken;
}

function audienceMatches(aud: JwtClaims["aud"]): boolean {
    return Array.isArray(aud) ? aud.includes(GOOGLE_CLIENT_ID) : aud === GOOGLE_CLIENT_ID;
}

// ── JWT helpers ───────────────────────────────────────────

export function isExpired(token: string): boolean {
    const exp = parseJwtPayload(token)?.exp;
    if (typeof exp !== "number") return true;
    return exp - EXPIRY_MARGIN_SECONDS <= Date.now() / 1000;
}

export function getAccountName(token: string): string | undefined {
    const claims = parseJwtPayload(token);
    return claims?.email || claims?.name || undefined;
}

/** Decodes (does not verify) a JWT payload. Returns null for malformed tokens. */
export function parseJwtPayload(token: string): JwtClaims | null {
    try {
        const segment = token.split(".")[1];
        if (!segment) return null;

        let base64 = segment.replace(/-/g, "+").replace(/_/g, "/");
        base64 += "=".repeat((4 - (base64.length % 4)) % 4);

        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const payload: unknown = JSON.parse(new TextDecoder().decode(bytes));
        return typeof payload === "object" && payload !== null ? payload as JwtClaims : null;
    } catch {
        return null;
    }
}
