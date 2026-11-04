declare const __CONFIG__: {
    API_URL: string;
    GOOGLE_CLIENT_ID: string;
    DEV_SIGN_IN: boolean;
    LIVE_RELOAD: boolean;
};

// Compiled in by build.mjs; see docs/components/web-app.md for the values.

/** The packets endpoint, not the API root. */
export const API_URL = __CONFIG__.API_URL;

/** The Google OAuth web client id; empty only in a development build with DEV_SIGN_IN. */
export const GOOGLE_CLIENT_ID = __CONFIG__.GOOGLE_CLIENT_ID;

/**
 * Whether the sign-in page offers signing in as a development user, through the API's Dev scheme instead of
 * Google. Only a development build (`--dev`) with DEV_SIGN_IN=true has it; a minified build never does.
 */
export const DEV_SIGN_IN = __CONFIG__.DEV_SIGN_IN;

/** Whether the page reloads when the dev server has rebuilt it. */
export const LIVE_RELOAD = __CONFIG__.LIVE_RELOAD;
