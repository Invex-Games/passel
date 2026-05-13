import {GOOGLE_CLIENT_ID} from "./config.js";

export async function getIdToken(): Promise<string | null> {
    const result = await chrome.storage.local.get("idToken");
    return result.idToken || null;
}

export async function removeIdToken(): Promise<void> {
    await chrome.storage.local.remove("idToken");
}

export async function signIn(): Promise<string> {
    const redirectUri = chrome.identity.getRedirectURL();
    const nonce = crypto.randomUUID();

    const authUrl = new URL("https://accounts.google.com/o/oauth2/v2/auth");
    authUrl.searchParams.set("client_id", GOOGLE_CLIENT_ID);
    authUrl.searchParams.set("response_type", "id_token");
    authUrl.searchParams.set("redirect_uri", redirectUri);
    authUrl.searchParams.set("scope", "openid email profile");
    authUrl.searchParams.set("nonce", nonce);
    authUrl.searchParams.set("prompt", "select_account");

    const responseUrl = await chrome.identity.launchWebAuthFlow({
        url: authUrl.toString(), interactive: true,
    });

    if (!responseUrl) throw new Error("No response from auth flow");

    const params = new URLSearchParams(new URL(responseUrl).hash.substring(1));
    const idToken = params.get("id_token");
    if (!idToken) throw new Error("No ID token received");

    await chrome.storage.local.set({idToken});
    return idToken;
}

export function parseJwtPayload(token: string): Record<string, unknown> {
    const base64 = token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
    return JSON.parse(atob(base64));
}

