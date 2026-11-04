import {
    accountName,
    audienceIncludes,
    devToken,
    DEV_USER_RULE,
    GOOGLE_ISSUERS,
    isTokenExpired,
    isValidDevUser,
    parseJwtPayload,
} from "@passel/core";

// Who is signed in to this tab. A Google ID token lives in session storage: it survives a reload and goes with the
// tab, and the API is the one to verify it. A hint that someone signed in with Google lives in local storage, so a
// new tab can offer One Tap to the same person; there is no other way to mint a new token without them.
//
// A development build with DEV_SIGN_IN can instead sign in as a development user (the API's Dev scheme): the
// user's name is the whole session, kept in local storage until sign-out, along with the name last used, which the
// sign-in page offers again. No browser APIs here beyond the storage passed in, so it is unit-tested.

/** The part of the Web Storage API the session uses. */
export type StringStore = Pick<Storage, "getItem" | "setItem" | "removeItem">;

const TOKEN_KEY = "passel.idToken";
const SIGNED_IN_KEY = "passel.signedInWithGoogle";
const DEV_USER_KEY = "passel.devUser";
const LAST_DEV_USER_KEY = "passel.lastDevUser";

/** Why a signed-in user was signed out without asking. */
export const SESSION_EXPIRED_MESSAGE = "Your session has expired. Sign in again.";

export interface SessionOptions {
    /** The Google web client id tokens must be issued for; empty when Google sign-in isn't configured. */
    clientId: string;
    devSignIn: boolean;
    /** Holds the token: session storage in the browser. */
    tabStore: StringStore;
    /** Holds the hints that outlive the tab: local storage in the browser. */
    browserStore: StringStore;
    now?: () => number;
}

export class SessionError extends Error {
}

export class Session {
    /** Whether the last token asked for had expired: the user was signed in and the session ran out. */
    private expired = false;

    constructor(private readonly options: SessionOptions) {
    }

    /** The token to send, or null when nobody is signed in or the token has expired. */
    token(): string | null {
        const devUser = this.devUser();
        if (devUser) return devToken(devUser);

        const token = this.options.tabStore.getItem(TOKEN_KEY);
        if (!token) return null;

        // No margin: the API decides, and a stream it opened ends when the token does
        if (isTokenExpired(token, this.now(), 0)) {
            this.options.tabStore.removeItem(TOKEN_KEY);
            this.expired = true;
            return null;
        }
        return token;
    }

    /** The signed-in account as shown to the user, or null. */
    account(): string | null {
        const token = this.token();
        return token ? accountName(token) ?? "Signed in" : null;
    }

    /** Whether the session ran out (rather than the user signing out); cleared by the next sign-in. */
    get hasExpired(): boolean {
        return this.expired;
    }

    /** Whether someone signed in with Google in this browser and hasn't signed out, so One Tap may sign them back in. */
    get returning(): boolean {
        return this.options.browserStore.getItem(SIGNED_IN_KEY) === "true";
    }

    /** The development user last signed in as, to offer again; null outside a DEV_SIGN_IN build. */
    get lastDevUser(): string | null {
        return this.options.devSignIn ? this.options.browserStore.getItem(LAST_DEV_USER_KEY) : null;
    }

    /**
     * Signs in with an ID token from Google Identity Services. Refuses one that was plainly not issued to this app
     * for this sign-in: the wrong audience or issuer, a nonce other than the one asked for, or already expired.
     * Returns the account name. The signature is the API's to check.
     */
    acceptGoogleCredential(credential: string, nonce: string | null): string {
        const claims = parseJwtPayload(credential);
        if (!claims) throw new SessionError("Google sent a sign-in Passel couldn't read. Try again.");
        if (!audienceIncludes(claims.aud, this.options.clientId) || !GOOGLE_ISSUERS.includes(claims.iss ?? "")) {
            throw new SessionError("Google sent a sign-in meant for another app.");
        }
        if (nonce !== null && claims.nonce !== nonce) throw new SessionError("That sign-in was for an earlier attempt. Try again.");
        if (isTokenExpired(credential, this.now(), 0)) throw new SessionError("That sign-in has already expired. Try again.");

        this.options.browserStore.removeItem(DEV_USER_KEY);
        this.options.tabStore.setItem(TOKEN_KEY, credential);
        this.options.browserStore.setItem(SIGNED_IN_KEY, "true");
        this.expired = false;
        return accountName(credential) ?? "Signed in";
    }

    /** Signs in as a development user (DEV_SIGN_IN builds only). Returns the account name. */
    signInAsDevUser(name: string): string {
        if (!this.options.devSignIn) throw new SessionError("This build can't sign in as a development user.");

        const user = name.trim();
        if (!isValidDevUser(user)) throw new SessionError(`A development user ${DEV_USER_RULE}.`);

        this.options.tabStore.removeItem(TOKEN_KEY);
        this.options.browserStore.removeItem(SIGNED_IN_KEY);
        this.options.browserStore.setItem(DEV_USER_KEY, user);
        this.options.browserStore.setItem(LAST_DEV_USER_KEY, user);
        this.expired = false;
        return accountName(devToken(user)) ?? user;
    }

    /** Signs out: forgets the token, the development user and the hint for One Tap. */
    signOut(): void {
        this.options.tabStore.removeItem(TOKEN_KEY);
        this.options.browserStore.removeItem(DEV_USER_KEY);
        this.options.browserStore.removeItem(SIGNED_IN_KEY);
        this.expired = false;
    }

    /**
     * Forgets a token the API refused, keeping the hint for One Tap: the user didn't sign out, so signing back in
     * may need no more than a tap.
     */
    drop(): void {
        if (this.devUser()) {
            this.options.browserStore.removeItem(DEV_USER_KEY);
            return;
        }
        if (this.options.tabStore.getItem(TOKEN_KEY)) {
            this.options.tabStore.removeItem(TOKEN_KEY);
            this.expired = true;
        }
    }

    private devUser(): string | null {
        return this.options.devSignIn ? this.options.browserStore.getItem(DEV_USER_KEY) : null;
    }

    private now(): number {
        return (this.options.now ?? Date.now)();
    }
}
