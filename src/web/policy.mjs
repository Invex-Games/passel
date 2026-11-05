// The Content-Security-Policy headers the site is served with, for the dev server in build.mjs. The web image sends
// the same policies from deploy/web/Caddyfile, and static-pages.test.ts checks that the two agree.

const GOOGLE_IDENTITY = "https://accounts.google.com/gsi/";

/** Every page but the app's: the landing, privacy, terms and not-found pages run no script at all. */
export const PAGE_CSP = [
    "default-src 'self'",
    "script-src 'none'",
    "object-src 'none'",
    "base-uri 'none'",
    "form-action 'none'",
    "frame-ancestors 'none'",
].join("; ");

/**
 * The app's policy (everything under /app/). Scripts come from this origin and Google Identity Services only;
 * requests go to this origin (its configuration among them), the API and Google; preview images come from whatever
 * site a link points at, over https. The GIS entries are the ones Google documents for its button and One Tap.
 */
export function appCsp(apiOrigin) {
    return [
        "default-src 'self'",
        `script-src 'self' ${GOOGLE_IDENTITY}client`,
        `style-src 'self' ${GOOGLE_IDENTITY}style`,
        `frame-src ${GOOGLE_IDENTITY}`,
        `connect-src 'self' ${apiOrigin} ${GOOGLE_IDENTITY}`,
        "img-src 'self' data: https:",
        "object-src 'none'",
        "base-uri 'none'",
        "form-action 'none'",
        "frame-ancestors 'none'",
    ].join("; ");
}

/** Which policy a request path gets: the app's below /app/, the pages' everywhere else. */
export function cspFor(path, apiOrigin) {
    return path.startsWith("/app/") ? appCsp(apiOrigin) : PAGE_CSP;
}
