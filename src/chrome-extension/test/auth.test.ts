import {beforeEach, describe, expect, it, vi} from "vitest";
import {installChrome, REDIRECT_URL, type FakeChrome} from "./chrome.js";
import {CLIENT_ID, idToken, inMinutes, jwt} from "./tokens.js";

// The build-time config, changed by the tests that need a development build or one without a client id.
const config = vi.hoisted(() => ({googleClientId: "", devSignIn: false}));
vi.mock("../src/config.js", () => ({
    API_URL: "https://api.passel.test/api/packets",
    get GOOGLE_CLIENT_ID() {
        return config.googleClientId;
    },
    get DEV_SIGN_IN() {
        return config.devSignIn;
    },
}));

import {
    getAccountName,
    getLastDevUser,
    getStoredToken,
    getToken,
    isExpired,
    onSessionChanged,
    parseJwtPayload,
    refreshToken,
    signIn,
    signOut,
} from "../src/auth.js";

const CACHED_FEED = {packets: [], nextCursor: null, lastSyncedAt: 1};

// The module is loaded once, so the listener stays registered; mockReset clears its calls for each test.
const sessionChanged = vi.fn();
onSessionChanged(() => sessionChanged());

let fake: FakeChrome;

beforeEach(() => {
    config.googleClientId = CLIENT_ID;
    config.devSignIn = false;
    fake = installChrome();
    vi.spyOn(console, "warn").mockImplementation(() => {
    });
    vi.spyOn(console, "error").mockImplementation(() => {
    });
});

/** Answers every auth flow with this redirect fragment, built from the request Google was sent. */
function googleAnswers(fragment: (request: URL) => Record<string, string>): void {
    fake.identity.launchWebAuthFlow.mockImplementation(async ({url}) => {
        const request = new URL(url);
        return `${REDIRECT_URL}#${new URLSearchParams(fragment(request))}`;
    });
}

/** Answers every auth flow as Google would for an account with these claims. */
function googleSignsIn(claims: Record<string, unknown> = {}): void {
    googleAnswers(request => ({
        id_token: idToken({nonce: request.searchParams.get("nonce"), ...claims}),
        state: request.searchParams.get("state") ?? "",
    }));
}

function authRequests(): { url: URL; interactive: boolean | undefined }[] {
    return fake.identity.launchWebAuthFlow.mock.calls.map(([details]) => ({
        url: new URL(details.url),
        interactive: details.interactive,
    }));
}

describe("signing in with Google", () => {
    it("asks Google for an ID token, always showing the account picker", async () => {
        fake.storage.local.data.loginHint = "old@example.com";
        googleSignsIn({email: "old@example.com"});

        const token = await signIn();

        const [{url, interactive}] = authRequests();
        expect(interactive).toBe(true);
        expect(url.origin + url.pathname).toBe("https://accounts.google.com/o/oauth2/v2/auth");
        expect(url.searchParams.get("client_id")).toBe(CLIENT_ID);
        expect(url.searchParams.get("response_type")).toBe("id_token");
        expect(url.searchParams.get("redirect_uri")).toBe(REDIRECT_URL);
        expect(url.searchParams.get("scope")).toBe("openid email profile");
        expect(url.searchParams.get("nonce")).toMatch(/^[0-9a-f-]{36}$/);
        expect(url.searchParams.get("state")).toMatch(/^[0-9a-f-]{36}$/);
        expect(url.searchParams.get("prompt")).toBe("select_account");
        // The picker is the point of an explicit sign-in, so the last account isn't suggested.
        expect(url.searchParams.has("login_hint")).toBe(false);

        expect(fake.storage.session.data.idToken).toBe(token);
        expect(fake.storage.local.data.loginHint).toBe("old@example.com");
    });

    it("keeps the email as the login hint, or the subject when there is no email", async () => {
        googleSignsIn({email: undefined, sub: "1001"});
        await signIn();
        expect(fake.storage.local.data.loginHint).toBe("1001");
    });

    it("drops the cached feed when a different account signs in, and keeps it for the same one", async () => {
        fake.storage.local.data = {loginHint: "ann@example.com", packetsCache: CACHED_FEED};

        googleSignsIn({email: "ann@example.com"});
        await signIn();
        expect(fake.storage.local.data.packetsCache).toEqual(CACHED_FEED);

        googleSignsIn({email: "bob@example.com"});
        await signIn();
        expect(fake.storage.local.data.packetsCache).toBeUndefined();
        expect(fake.storage.local.data.loginHint).toBe("bob@example.com");
    });

    it.each([
        ["an error from Google, with its description", (request: URL) => ({
            error: "access_denied", error_description: "The user said no", state: request.searchParams.get("state")!,
        }), "The user said no"],
        ["an error from Google, without a description", () => ({error: "access_denied"}), "access_denied"],
        ["a state that isn't the one sent", (request: URL) => ({
            id_token: idToken({nonce: request.searchParams.get("nonce")}), state: "forged",
        }), "Sign-in response state mismatch"],
        ["no ID token", (request: URL) => ({state: request.searchParams.get("state")!}), "No ID token received"],
        ["a token that isn't a JWT", (request: URL) => ({
            id_token: "not-a-jwt", state: request.searchParams.get("state")!,
        }), "Malformed ID token"],
        ["a nonce that isn't the one sent", (request: URL) => ({
            id_token: idToken({nonce: "replayed"}), state: request.searchParams.get("state")!,
        }), "ID token nonce mismatch"],
        ["a token for another client", (request: URL) => ({
            id_token: idToken({nonce: request.searchParams.get("nonce"), aud: "someone-else"}),
            state: request.searchParams.get("state")!,
        }), "ID token audience mismatch"],
        ["a token that expires within the margin", (request: URL) => ({
            id_token: idToken({nonce: request.searchParams.get("nonce"), exp: inMinutes(4)}),
            state: request.searchParams.get("state")!,
        }), "ID token is expired"],
    ])("refuses %s", async (_, fragment, message) => {
        googleAnswers(fragment);

        await expect(signIn()).rejects.toThrow(message);
        expect(fake.storage.session.data.idToken).toBeUndefined();
        expect(fake.storage.local.data.loginHint).toBeUndefined();
    });

    it("accepts a token whose audience is a list that includes this client", async () => {
        googleSignsIn({aud: ["another-client", CLIENT_ID]});
        await expect(signIn()).resolves.toBeTypeOf("string");
    });

    it("refuses an empty answer from the auth flow", async () => {
        fake.identity.launchWebAuthFlow.mockResolvedValue(undefined);
        await expect(signIn()).rejects.toThrow("No response from auth flow");
    });

    it("refuses to start without a client id", async () => {
        config.googleClientId = "";
        await expect(signIn()).rejects.toThrow("Google sign-in isn't configured in this build.");
        expect(fake.identity.launchWebAuthFlow).not.toHaveBeenCalled();
    });

    it("runs one auth flow at a time, sharing it between callers", async () => {
        googleSignsIn();
        const [first, second] = await Promise.all([signIn(), signIn()]);

        expect(first).toBe(second);
        expect(fake.identity.launchWebAuthFlow).toHaveBeenCalledTimes(1);
    });
});

describe("tokens", () => {
    it("returns the stored token while it has more than five minutes left", async () => {
        const token = idToken({exp: inMinutes(6)});
        fake.storage.session.data.idToken = token;

        expect(await getStoredToken()).toBe(token);
        expect(await getToken(false)).toBe(token);
        expect(fake.identity.launchWebAuthFlow).not.toHaveBeenCalled();
    });

    it("treats a token with less than five minutes left as gone", async () => {
        fake.storage.session.data.idToken = idToken({exp: inMinutes(4)});
        expect(await getStoredToken()).toBeNull();
    });

    it("refreshes silently with the login hint when the stored token has expired", async () => {
        fake.storage.session.data.idToken = idToken({exp: inMinutes(-1)});
        fake.storage.local.data.loginHint = "ann@example.com";
        googleSignsIn();

        const token = await getToken(false);

        expect(token).toBe(fake.storage.session.data.idToken);
        const [{url, interactive}] = authRequests();
        expect(interactive).toBe(false);
        expect(url.searchParams.get("prompt")).toBe("none");
        expect(url.searchParams.get("login_hint")).toBe("ann@example.com");
    });

    it("falls back to an interactive sign-in, without forcing the picker, when the silent one fails", async () => {
        fake.storage.local.data.loginHint = "ann@example.com";
        let calls = 0;
        googleAnswers((request): Record<string, string> => ++calls === 1
            ? {error: "interaction_required", state: request.searchParams.get("state")!}
            : {id_token: idToken({nonce: request.searchParams.get("nonce")}), state: request.searchParams.get("state")!});

        expect(await refreshToken(true)).toBeTypeOf("string");

        const [silent, reauth] = authRequests();
        expect(silent.url.searchParams.get("prompt")).toBe("none");
        expect(reauth.interactive).toBe(true);
        expect(reauth.url.searchParams.has("prompt")).toBe(false);
        expect(reauth.url.searchParams.get("login_hint")).toBe("ann@example.com");
    });

    it("never shows a window when not interactive: a failed silent refresh is null", async () => {
        fake.storage.local.data.loginHint = "ann@example.com";
        googleAnswers(request => ({error: "interaction_required", state: request.searchParams.get("state")!}));

        expect(await refreshToken(false)).toBeNull();
        expect(authRequests().map(r => r.interactive)).toEqual([false]);
    });

    it("doesn't try a silent refresh for a signed-out user", async () => {
        expect(await refreshToken(false)).toBeNull();
        expect(fake.identity.launchWebAuthFlow).not.toHaveBeenCalled();
    });

    it("answers null when the interactive sign-in fails too", async () => {
        fake.identity.launchWebAuthFlow.mockRejectedValue(new Error("The user did not approve access."));
        expect(await refreshToken(true)).toBeNull();
    });

    it("discards the stored token before refreshing", async () => {
        fake.storage.session.data.idToken = idToken();
        await refreshToken(false);
        expect(fake.storage.session.data.idToken).toBeUndefined();
    });
});

describe("telling the service worker the session changed", () => {
    it("tells it after a sign-out, once everything is forgotten", async () => {
        fake.storage.local.data = {loginHint: "ann@example.com", packetsCache: CACHED_FEED};
        sessionChanged.mockImplementation(() => {
            expect(fake.storage.local.data).toEqual({});
        });

        await signOut();

        expect(sessionChanged).toHaveBeenCalledTimes(1);
    });

    it("tells it after an explicit sign-in, once the token is stored, even as the same account", async () => {
        fake.storage.local.data.loginHint = "ann@example.com";
        googleSignsIn({email: "ann@example.com"});
        sessionChanged.mockImplementation(() => {
            expect(fake.storage.session.data.idToken).toBeTypeOf("string");
        });

        await signIn();

        expect(sessionChanged).toHaveBeenCalledTimes(1);
    });

    it("tells it after an interactive sign-in that a send needed, as from a shortcut while signed out", async () => {
        googleSignsIn();

        expect(await getToken(true)).toBeTypeOf("string");
        expect(sessionChanged).toHaveBeenCalledTimes(1);
    });

    it("doesn't tell it about a silent refresh of the same account", async () => {
        fake.storage.local.data.loginHint = "ann@example.com";
        googleSignsIn({email: "ann@example.com"});

        await refreshToken(false);

        expect(sessionChanged).not.toHaveBeenCalled();
    });

    it("tells it when a silent refresh comes back as another account", async () => {
        fake.storage.local.data.loginHint = "ann@example.com";
        googleSignsIn({email: "bob@example.com"});

        await refreshToken(false);

        expect(sessionChanged).toHaveBeenCalledTimes(1);
    });

    it("doesn't tell it about a sign-in that failed", async () => {
        fake.identity.launchWebAuthFlow.mockRejectedValue(new Error("The user did not approve access."));

        await expect(signIn()).rejects.toThrow();

        expect(sessionChanged).not.toHaveBeenCalled();
    });

    it("tells it after signing in as a development user", async () => {
        config.devSignIn = true;

        await signIn("alice");

        expect(sessionChanged).toHaveBeenCalledTimes(1);
    });
});

describe("signing out", () => {
    it("forgets the token, the login hint, the development user and the cached feed", async () => {
        fake.storage.session.data.idToken = idToken();
        fake.storage.local.data = {loginHint: "ann@example.com", devUser: "alice", lastDevUser: "alice", packetsCache: CACHED_FEED};

        await signOut();

        expect(fake.storage.session.data).toEqual({});
        // The development user last used is kept, for the popup to offer again.
        expect(fake.storage.local.data).toEqual({lastDevUser: "alice"});
    });
});

describe("development users", () => {
    it("can't be signed in as outside a DEV_SIGN_IN build", async () => {
        await expect(signIn("alice")).rejects.toThrow("This build can't sign in as a development user.");
    });

    it("ignores a stored development user outside a DEV_SIGN_IN build", async () => {
        fake.storage.local.data = {devUser: "alice", lastDevUser: "alice"};
        expect(await getStoredToken()).toBeNull();
        expect(await getLastDevUser()).toBeNull();
    });

    describe("in a DEV_SIGN_IN build", () => {
        beforeEach(() => {
            config.devSignIn = true;
        });

        it("signs in as the named user, replacing a Google session", async () => {
            fake.storage.session.data.idToken = idToken();
            fake.storage.local.data = {loginHint: "ann@example.com", packetsCache: CACHED_FEED};

            const token = await signIn("  alice ");

            expect(token).toBe("dev-user:alice");
            expect(fake.storage.session.data.idToken).toBeUndefined();
            // Another account than the Google one, so its packets must not be shown.
            expect(fake.storage.local.data).toEqual({devUser: "alice", lastDevUser: "alice"});
            expect(await getStoredToken()).toBe("dev-user:alice");
            expect(await getLastDevUser()).toBe("alice");
            expect(fake.identity.launchWebAuthFlow).not.toHaveBeenCalled();
        });

        it("keeps the cached feed for the same user and drops it for another", async () => {
            fake.storage.local.data = {devUser: "alice", packetsCache: CACHED_FEED};

            await signIn("alice");
            expect(fake.storage.local.data.packetsCache).toEqual(CACHED_FEED);

            await signIn("bob");
            expect(fake.storage.local.data.packetsCache).toBeUndefined();
        });

        it("refuses a name the API wouldn't take", async () => {
            await expect(signIn("not allowed")).rejects.toThrow("A development user must be 1 to 255 letters");
            expect(fake.storage.local.data).toEqual({});
        });

        it("still offers the last development user after signing out", async () => {
            await signIn("alice");
            await signOut();

            expect(await getStoredToken()).toBeNull();
            expect(await getLastDevUser()).toBe("alice");
        });
    });
});

describe("JWT helpers", () => {
    it("decodes base64url payloads, including UTF-8 text", () => {
        expect(parseJwtPayload(jwt({name: "Zoë ✓", sub: "1"}))).toEqual({name: "Zoë ✓", sub: "1"});
    });

    it.each([
        ["no payload segment", "header"],
        ["a payload that isn't base64", "a.!!!.c"],
        ["a payload that isn't JSON", `a.${btoa("not json")}.c`],
        ["a payload that isn't an object", `a.${btoa("42")}.c`],
    ])("returns null for %s", (_, token) => {
        expect(parseJwtPayload(token)).toBeNull();
    });

    it("treats a token without an expiry as expired, and a development token as never expiring", () => {
        expect(isExpired(jwt({sub: "1"}))).toBe(true);
        expect(isExpired(idToken({exp: inMinutes(10)}))).toBe(false);
        expect(isExpired("dev-user:alice")).toBe(false);
    });

    it("names the account by email, then name, and marks a development user", () => {
        expect(getAccountName(idToken({email: "ann@example.com", name: "Ann"}))).toBe("ann@example.com");
        expect(getAccountName(idToken({email: undefined, name: "Ann"}))).toBe("Ann");
        expect(getAccountName(jwt({sub: "1"}))).toBeUndefined();
        expect(getAccountName("dev-user:alice")).toBe("alice (dev)");
    });
});
