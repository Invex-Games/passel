declare const __FLAGS__: {
    DEV_SIGN_IN: boolean;
    LIVE_RELOAD: boolean;
};

// Compiled in by build.mjs. They depend on how the site was built, never on where it runs: a production build has
// both off. The API address and the client id are not here; they come from /app/config.json (config.ts).

/**
 * Whether the sign-in page offers signing in as a development user, through the API's Dev scheme instead of
 * Google. Only a development build (`--dev`) with DEV_SIGN_IN=true has it; a minified build never does.
 */
export const DEV_SIGN_IN = __FLAGS__.DEV_SIGN_IN;

/** Whether the page reloads when the dev server has rebuilt it. */
export const LIVE_RELOAD = __FLAGS__.LIVE_RELOAD;
