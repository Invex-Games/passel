import type {StringStore} from "../src/app/session.js";

// Stand-ins for the browser APIs the app's logic uses, shared by the tests.

/** Web Storage in memory. */
export class MemoryStore implements StringStore {
    readonly items = new Map<string, string>();

    getItem(key: string): string | null {
        return this.items.get(key) ?? null;
    }

    setItem(key: string, value: string): void {
        this.items.set(key, value);
    }

    removeItem(key: string): void {
        this.items.delete(key);
    }
}

export const CLIENT_ID = "web-client.apps.googleusercontent.com";

/** An unsigned Google-style ID token with these claims over sensible defaults. */
export function googleToken(claims: Record<string, unknown> = {}, nowMs = Date.now()): string {
    const payload = {
        iss: "https://accounts.google.com",
        aud: CLIENT_ID,
        sub: "1234567890",
        email: "alice@example.com",
        exp: Math.floor(nowMs / 1000) + 3600,
        ...claims,
    };
    const segment = (value: unknown) => Buffer.from(JSON.stringify(value)).toString("base64url");
    return `${segment({alg: "RS256"})}.${segment(payload)}.signature`;
}
