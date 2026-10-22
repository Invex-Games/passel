// How a token becomes the Authorization header of an API request. Tokens are Google ID tokens, sent as
// "Bearer <token>". The one exception is a development build with DEV_SIGN_IN: the worker then holds a
// token it made itself, marked with DEV_TOKEN_PREFIX, and sends "Dev <user>", which the API accepts only
// while it runs in Development. Different users are different accounts, which is how per-user behaviour
// gets tested from one browser. No chrome.* APIs here, so it can be unit-tested.

/** Marks a token the extension issued for the API's Dev scheme. */
export const DEV_TOKEN_PREFIX = "dev-user:";

/** What the API accepts as a development user id: 1 to 255 ASCII letters, digits or -._@+ characters. */
export const DEV_USER_PATTERN = /^[A-Za-z0-9._@+-]{1,255}$/;

/** The problem shown for a user id that fails DEV_USER_PATTERN. */
export const DEV_USER_RULE = "must be 1 to 255 letters, digits or -._@+ characters";

export function isValidDevUser(user: string): boolean {
    return DEV_USER_PATTERN.test(user);
}

/** The token the extension holds for the development user. */
export function devToken(user: string): string {
    return `${DEV_TOKEN_PREFIX}${user}`;
}

/** The user of a development token, or null for a Google ID token. */
export function devUserOf(token: string): string | null {
    return token.startsWith(DEV_TOKEN_PREFIX) ? token.slice(DEV_TOKEN_PREFIX.length) : null;
}

/** "Dev <user>" for a development token, otherwise "Bearer <token>". */
export function authorizationHeader(token: string): string {
    const user = devUserOf(token);
    return user === null ? `Bearer ${token}` : `Dev ${user}`;
}
