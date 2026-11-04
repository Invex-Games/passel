import {beforeEach, describe, expect, it} from "vitest";
import {Session, SessionError} from "../src/app/session.js";
import {CLIENT_ID, googleToken, MemoryStore} from "./fakes.js";

let now: number;
let tab: MemoryStore;
let browser: MemoryStore;

function session(devSignIn = false): Session {
    return new Session({clientId: CLIENT_ID, devSignIn, tabStore: tab, browserStore: browser, now: () => now});
}

beforeEach(() => {
    now = Date.UTC(2026, 10, 4, 12);
    tab = new MemoryStore();
    browser = new MemoryStore();
});

describe("Google sign-in", () => {
    it("keeps the token for the tab and remembers that this browser signed in with Google", () => {
        const token = googleToken({}, now);

        expect(session().acceptGoogleCredential(token, null)).toBe("alice@example.com");

        // A reload makes a new Session over the same storage
        const reloaded = session();
        expect(reloaded.token()).toBe(token);
        expect(reloaded.account()).toBe("alice@example.com");
        expect(reloaded.returning).toBe(true);
    });

    it("refuses a token for another audience or issuer, with another nonce, or already expired", () => {
        const s = session();

        expect(() => s.acceptGoogleCredential(googleToken({aud: "someone-else"}, now), null)).toThrow(SessionError);
        expect(() => s.acceptGoogleCredential(googleToken({iss: "https://evil.test"}, now), null)).toThrow("another app");
        expect(() => s.acceptGoogleCredential(googleToken({nonce: "old"}, now), "new")).toThrow("earlier attempt");
        expect(() => s.acceptGoogleCredential(googleToken({exp: now / 1000 - 1}, now), null)).toThrow("expired");
        expect(() => s.acceptGoogleCredential("not a token", null)).toThrow("couldn't read");

        expect(s.token()).toBeNull();
        expect(s.returning).toBe(false);
    });

    it("accepts the nonce it asked for and an audience list that includes the client", () => {
        const token = googleToken({nonce: "n-1", aud: ["other", CLIENT_ID]}, now);

        expect(session().acceptGoogleCredential(token, "n-1")).toBe("alice@example.com");
    });

    it("refuses every Google token when no client id is configured", () => {
        const s = new Session({clientId: "", devSignIn: true, tabStore: tab, browserStore: browser, now: () => now});

        expect(() => s.acceptGoogleCredential(googleToken({aud: ""}, now), null)).toThrow(SessionError);
    });

    it("drops a token once it has expired, and says the session ran out", () => {
        const s = session();
        s.acceptGoogleCredential(googleToken({}, now), null);

        now += 3600 * 1000;

        expect(s.token()).toBeNull();
        expect(s.account()).toBeNull();
        expect(s.hasExpired).toBe(true);
        // Not a sign-out: One Tap may still bring the user back
        expect(s.returning).toBe(true);
    });

    it("drop forgets a refused token but keeps the hint; signOut forgets both", () => {
        const s = session();
        s.acceptGoogleCredential(googleToken({}, now), null);

        s.drop();
        expect(s.token()).toBeNull();
        expect(s.hasExpired).toBe(true);
        expect(s.returning).toBe(true);

        s.acceptGoogleCredential(googleToken({}, now), null);
        expect(s.hasExpired).toBe(false);

        s.signOut();
        expect(s.token()).toBeNull();
        expect(s.returning).toBe(false);
        expect(s.hasExpired).toBe(false);
    });
});

describe("development users", () => {
    it("signs in as a development user and offers the name again after sign-out", () => {
        const s = session(true);

        expect(s.signInAsDevUser("  alice ")).toBe("alice (dev)");
        expect(s.token()).toBe("dev-user:alice");
        expect(session(true).token()).toBe("dev-user:alice");

        s.signOut();
        expect(s.token()).toBeNull();
        expect(s.lastDevUser).toBe("alice");
    });

    it("refuses an invalid name, and any name in a build without DEV_SIGN_IN", () => {
        expect(() => session(true).signInAsDevUser("not valid")).toThrow("must be 1 to 255");
        expect(() => session(false).signInAsDevUser("alice")).toThrow("can't sign in as a development user");
    });

    it("a build without DEV_SIGN_IN ignores a development user left in storage", () => {
        session(true).signInAsDevUser("alice");

        const production = session(false);
        expect(production.token()).toBeNull();
        expect(production.lastDevUser).toBeNull();
    });

    it("switching between Google and a development user leaves only the new one signed in", () => {
        const s = session(true);
        s.acceptGoogleCredential(googleToken({}, now), null);

        s.signInAsDevUser("bob");
        expect(s.token()).toBe("dev-user:bob");
        expect(s.returning).toBe(false);

        const token = googleToken({}, now);
        s.acceptGoogleCredential(token, null);
        expect(s.token()).toBe(token);
    });

    it("drop signs a refused development user out", () => {
        const s = session(true);
        s.signInAsDevUser("alice");

        s.drop();

        expect(s.token()).toBeNull();
        expect(s.hasExpired).toBe(false);
    });
});
