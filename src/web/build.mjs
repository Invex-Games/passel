import * as esbuild from "esbuild";
import {cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync} from "fs";

// Produces the static site in OUT_DIR: the landing, privacy and terms pages as plain HTML with no script, and the
// web app under app/ (index.html, app.js, styles.css). Any static host can serve the folder as it is; see
// docs/components/web-app.md for the paths it expects. WEB_APP_OUT_DIR builds elsewhere, which the tests use.
const OUT_DIR = process.env.WEB_APP_OUT_DIR || "dist";

// ── Load environment variables ───────────────────────────────
// Reads from .env file if present (local dev); values already set in process.env (CI / build tooling)
// take precedence, so a stale local .env can never override what the build was asked to use.

function loadEnv() {
    const env = {...process.env};

    const envFile = ".env";
    if (existsSync(envFile)) {
        const lines = readFileSync(envFile, "utf-8").split("\n");
        for (const line of lines) {
            const trimmed = line.trim();
            if (!trimmed || trimmed.startsWith("#")) continue;
            const eqIndex = trimmed.indexOf("=");
            if (eqIndex === -1) continue;
            const key = trimmed.slice(0, eqIndex).trim();
            if (process.env[key]) continue;
            env[key] = unquote(trimmed.slice(eqIndex + 1).trim());
        }
    }

    return env;
}

function unquote(value) {
    const quoted = value.length >= 2
        && (value[0] === "\"" || value[0] === "'")
        && value[value.length - 1] === value[0];
    return quoted ? value.slice(1, -1) : value;
}

const env = loadEnv();

function required(key) {
    const value = env[key];
    if (!value) {
        console.error(`Missing required environment variable: ${key}`);
        process.exit(1);
    }
    return value;
}

// ── Config values to inject ──────────────────────────────────

const isDev = process.argv.includes("--dev");
const isServe = process.argv.includes("--serve");
const port = Number(env.PORT || 5245);

const apiUrl = required("API_URL");

// Signing in as a development user (the API's Dev scheme, Development only) is compiled in for development builds
// that ask for it, and never for a minified build. Such a build can do without a Google client id; the sign-in page
// then offers only the development sign-in.
const devSignIn = isDev && env.DEV_SIGN_IN === "true";
const googleClientId = devSignIn ? env.GOOGLE_CLIENT_ID ?? "" : required("GOOGLE_CLIENT_ID");

let apiOrigin;
try {
    apiOrigin = new URL(apiUrl).origin;
} catch {
    console.error(`API_URL is not a valid URL: ${apiUrl}`);
    process.exit(1);
}

if (!Number.isInteger(port) || port <= 0 || port > 65535) {
    console.error(`PORT is not a port number: ${env.PORT}`);
    process.exit(1);
}

const define = {
    "__CONFIG__.API_URL": JSON.stringify(apiUrl),
    "__CONFIG__.GOOGLE_CLIENT_ID": JSON.stringify(googleClientId),
    "__CONFIG__.DEV_SIGN_IN": JSON.stringify(devSignIn),
    // The dev server tells the page when a rebuild finished, and the page reloads
    "__CONFIG__.LIVE_RELOAD": JSON.stringify(isDev && isServe),
};

// ── Static files ─────────────────────────────────────────────

const STATIC_DIR = "static";

// The app's Content-Security-Policy. Scripts come from this origin and Google Identity Services only; requests go
// to this origin, the API and Google; preview images come from whatever site a link points at, over https.
// The GIS entries are the ones Google documents for its sign-in button and One Tap.
const GOOGLE_IDENTITY = "https://accounts.google.com/gsi/";
const csp = [
    "default-src 'self'",
    `script-src 'self' ${GOOGLE_IDENTITY}client`,
    `style-src 'self' ${GOOGLE_IDENTITY}style`,
    `frame-src ${GOOGLE_IDENTITY}`,
    `connect-src 'self' ${apiOrigin} ${GOOGLE_IDENTITY}`,
    "img-src 'self' data: https:",
    "object-src 'none'",
    "base-uri 'none'",
    "form-action 'none'",
].join("; ");

// Google's sign-in needs the full referrer when a page on plain http://localhost talks to it, which is where --serve
// serves every build, minified or not; a deployed page on https keeps the browser's default.
const referrer = isServe ? "no-referrer-when-downgrade" : "strict-origin-when-cross-origin";

function writeAppPage() {
    const page = readFileSync(`${STATIC_DIR}/app/index.html`, "utf-8")
        .replace("%CSP%", csp)
        .replace("%REFERRER%", referrer);
    writeFileSync(`${OUT_DIR}/app/index.html`, page);
}

function copyStatic() {
    cpSync(STATIC_DIR, OUT_DIR, {recursive: true});
    writeAppPage();
}

// Re-copies static files after every (re)build so watch mode picks up HTML edits alongside the next code change.
const copyStaticPlugin = {
    name: "copy-static",
    setup(build) {
        build.onEnd(result => {
            if (result.errors.length === 0) copyStatic();
        });
    },
};

// ── Build options ────────────────────────────────────────────

const options = {
    entryPoints: [
        {in: "src/app/main.tsx", out: "app/app"},
        {in: "src/app/app.css", out: "app/styles"},
        {in: "src/site.css", out: "site"},
    ],
    bundle: true,
    format: "esm",
    target: "es2022",
    outdir: OUT_DIR,
    jsx: "automatic",
    jsxImportSource: "preact",
    define,
    minify: !isDev,
    sourcemap: isDev,
    plugins: [copyStaticPlugin],
};

// ── Run ──────────────────────────────────────────────────────

rmSync(OUT_DIR, {recursive: true, force: true});
mkdirSync(OUT_DIR, {recursive: true});

if (isServe) {
    const ctx = await esbuild.context(options);
    if (isDev) await ctx.watch();

    // Only this machine: the dev server has no TLS and the origin Google allows is http://localhost:<port>
    const {port: servedPort} = await ctx.serve({servedir: OUT_DIR, host: "localhost", port, fallback: `${OUT_DIR}/404.html`});
    console.log(`Serving ${OUT_DIR}/ at http://localhost:${servedPort}/ (the app is at /app/, the API at ${apiOrigin})`);
} else {
    await esbuild.build(options);
    console.log(`Built the web app to ${OUT_DIR}/ (API: ${apiOrigin})`);
}
