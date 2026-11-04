import {devUserOf} from "./authorization.js";

// Reading the claims of a Google ID token on the client: who signed in, for whom the token was issued and when it
// runs out. Nothing here verifies a signature; the API does that. A client only uses the claims to show the
// account, to refuse a token that was plainly not meant for it, and to stop sending one that has expired.

export interface JwtClaims {
    sub?: string;
    email?: string;
    name?: string;
    iss?: string;
    aud?: string | string[];
    exp?: number;
    nonce?: string;
}

/** Tokens with less than this much lifetime left are treated as expired. */
export const EXPIRY_MARGIN_SECONDS = 5 * 60;

/** The issuers of Google ID tokens, as the API accepts them. */
export const GOOGLE_ISSUERS: readonly string[] = ["https://accounts.google.com", "accounts.google.com"];

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

/**
 * Whether a token has expired, or will within `marginSeconds`. A development token has no lifetime (the API decides
 * whether it still takes it); a token without an expiry counts as expired.
 */
export function isTokenExpired(token: string, nowMs: number = Date.now(), marginSeconds = EXPIRY_MARGIN_SECONDS): boolean {
    if (devUserOf(token) !== null) return false;

    const exp = parseJwtPayload(token)?.exp;
    if (typeof exp !== "number") return true;
    return exp - marginSeconds <= nowMs / 1000;
}

/** Whether a token's audience includes `clientId`. */
export function audienceIncludes(aud: JwtClaims["aud"], clientId: string): boolean {
    if (!clientId) return false;
    return Array.isArray(aud) ? aud.includes(clientId) : aud === clientId;
}

/** The account a token stands for, as shown to the user: an email, a name, or "<user> (dev)" for a development user. */
export function accountName(token: string): string | undefined {
    const devUser = devUserOf(token);
    if (devUser !== null) return `${devUser} (dev)`;

    const claims = parseJwtPayload(token);
    return claims?.email || claims?.name || undefined;
}
