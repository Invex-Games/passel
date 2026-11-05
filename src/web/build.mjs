import * as esbuild from "esbuild";
import {cpSync, existsSync, mkdirSync, readFileSync, rmSync} from "fs";
import http from "node:http";
import {cspFor} from "./policy.mjs";

// Produces the static site in OUT_DIR: the landing, privacy and terms pages as plain HTML with no script, and the
// web app under app/ (index.html, app.js, styles.css). The site holds no environment's values: the app reads the
// API's address and the Google client id from /app/config.json when it starts, and the security policies are
// response headers. The web image provides both (deploy/web); --serve provides them from .env. See
// docs/components/web-app.md. WEB_APP_OUT_DIR builds elsewhere, which the tests use.
const OUT_DIR = process.env.WEB_APP_OUT_DIR || "dist";

// ── Load environment variables ───────────────────────────────
// Reads from .env file if present (local dev); values already set in process.env (the AppHost, tests) take
// precedence, so a stale local .env can never override what the dev server was asked to use.

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

function fail(message) {
    console.error(message);
    process.exit(1);
}

function required(key) {
    const value = env[key];
    if (!value) fail(`Missing required environment variable: ${key}`);
    return value;
}

// ── Build flags ──────────────────────────────────────────────

const isDev = process.argv.includes("--dev");
const isServe = process.argv.includes("--serve");

// Signing in as a development user (the API's Dev scheme, Development only) is compiled in for development builds
// that ask for it, and never for a minified build. Such a build can do without a Google client id; the sign-in page
// then offers only the development sign-in.
const devSignIn = isDev && env.DEV_SIGN_IN === "true";

const define = {
    "__FLAGS__.DEV_SIGN_IN": JSON.stringify(devSignIn),
    // The dev server tells the page when a rebuild finished, and the page reloads
    "__FLAGS__.LIVE_RELOAD": JSON.stringify(isDev && isServe),
};

// ── Static files ─────────────────────────────────────────────

const STATIC_DIR = "static";

function copyStatic() {
    cpSync(STATIC_DIR, OUT_DIR, {recursive: true});
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

// ── Dev server ───────────────────────────────────────────────
// What the web image does at runtime, from .env: /app/config.json for the app, and the security policies as headers.
// esbuild's server can't set headers, so it listens on a port of its own and this server stands in front of it.

function devConfig() {
    const apiUrl = required("API_URL");
    const googleClientId = devSignIn ? env.GOOGLE_CLIENT_ID ?? "" : required("GOOGLE_CLIENT_ID");

    let apiOrigin;
    try {
        apiOrigin = new URL(apiUrl).origin;
    } catch {
        fail(`API_URL is not a valid URL: ${apiUrl}`);
    }

    // The app checks these values again when it loads them, and shows what is wrong
    return {apiOrigin, json: JSON.stringify({API_URL: apiUrl, GOOGLE_CLIENT_ID: googleClientId})};
}

async function serve() {
    const port = Number(env.PORT || 5245);
    if (!Number.isInteger(port) || port <= 0 || port > 65535) fail(`PORT is not a port number: ${env.PORT}`);

    const {apiOrigin, json} = devConfig();

    const ctx = await esbuild.context(options);
    if (isDev) await ctx.watch();
    const esbuildServer = await ctx.serve({servedir: OUT_DIR, host: "127.0.0.1", fallback: `${OUT_DIR}/404.html`});

    const server = http.createServer((request, response) => {
        const path = new URL(request.url ?? "/", "http://localhost").pathname;
        const headers = {
            "Content-Security-Policy": cspFor(path, apiOrigin),
            // Google's sign-in needs the full referrer when a page on plain http://localhost talks to it; the web
            // image, always behind https, sends strict-origin-when-cross-origin
            "Referrer-Policy": "no-referrer-when-downgrade",
            "Cache-Control": "no-cache",
            "X-Content-Type-Options": "nosniff",
        };

        if (path === "/app/config.json") {
            response.writeHead(200, {...headers, "Content-Type": "application/json"});
            response.end(json);
            return;
        }

        const forwarded = http.request({
            host: esbuildServer.hosts[0] ?? "127.0.0.1",
            port: esbuildServer.port,
            path: request.url,
            method: request.method,
            headers: request.headers,
        }, upstream => {
            // Piped as it comes, so the /esbuild event stream that reloads the page passes through
            response.writeHead(upstream.statusCode ?? 502, {...upstream.headers, ...headers});
            upstream.pipe(response);
        });
        forwarded.on("error", () => {
            if (!response.headersSent) response.writeHead(502, headers);
            response.end();
        });
        request.pipe(forwarded);
    });

    // Only this machine: the dev server has no TLS and the origin Google allows is http://localhost:<port>
    server.listen(port, "localhost", () => {
        console.log(`Serving ${OUT_DIR}/ at http://localhost:${port}/ (the app is at /app/, the API at ${apiOrigin})`);
    });
}

// ── Run ──────────────────────────────────────────────────────

rmSync(OUT_DIR, {recursive: true, force: true});
mkdirSync(OUT_DIR, {recursive: true});

if (isServe) {
    await serve();
} else {
    await esbuild.build(options);
    console.log(`Built the web app to ${OUT_DIR}/; it reads its configuration from /app/config.json`);
}
