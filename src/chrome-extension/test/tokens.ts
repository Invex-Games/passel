// Unsigned JWTs for the tests: the extension decodes ID tokens but never verifies them (the API does).

export const CLIENT_ID = "client-1.apps.googleusercontent.com";

export function jwt(claims: Record<string, unknown>): string {
    return [{alg: "RS256", typ: "JWT"}, claims].map(base64Url).join(".") + ".signature";
}

/** A time `minutes` from now, as a JWT `exp`. */
export function inMinutes(minutes: number): number {
    return Math.floor(Date.now() / 1000) + minutes * 60;
}

/** An ID token for this client that is good for an hour. */
export function idToken(claims: Record<string, unknown> = {}): string {
    return jwt({sub: "1001", email: "ann@example.com", aud: CLIENT_ID, exp: inMinutes(60), ...claims});
}

function base64Url(value: unknown): string {
    const bytes = new TextEncoder().encode(JSON.stringify(value));
    return btoa(String.fromCharCode(...bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}
