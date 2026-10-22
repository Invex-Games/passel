import * as esbuild from "esbuild";
import {readFileSync, writeFileSync, existsSync, rmSync, mkdirSync, cpSync} from "fs";

// Produces a self-contained, loadable extension in OUT_DIR (manifest.json, popup.html, popup.css,
// icons/ and the compiled scripts). Load it unpacked for development, or zip its contents to publish.
const OUT_DIR = "dist";

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

const apiUrl = required("API_URL");
const googleClientId = required("GOOGLE_CLIENT_ID");

let apiOrigin;
try {
    apiOrigin = new URL(apiUrl).origin;
} catch {
    console.error(`API_URL is not a valid URL: ${apiUrl}`);
    process.exit(1);
}

const define = {
    "__CONFIG__.API_URL": JSON.stringify(apiUrl),
    "__CONFIG__.GOOGLE_CLIENT_ID": JSON.stringify(googleClientId),
};

// ── Static files ─────────────────────────────────────────────

// Static files (manifest, popup HTML/CSS, icons) live in STATIC_DIR rather than the project root, so the
// source folder can't be loaded unpacked by mistake: only OUT_DIR contains a manifest next to the compiled scripts.
const STATIC_DIR = "static";

// Host permissions are derived from API_URL so the extension can only reach its own API.
function writeManifest() {
    const manifest = JSON.parse(readFileSync(`${STATIC_DIR}/manifest.json`, "utf-8"));
    manifest.host_permissions = [`${apiOrigin}/*`];
    writeFileSync(`${OUT_DIR}/manifest.json`, JSON.stringify(manifest, null, 2));
}

function copyStatic() {
    cpSync(STATIC_DIR, OUT_DIR, {recursive: true});
    writeManifest();
}

// Re-copies static files after every (re)build so watch mode picks up HTML/CSS/manifest edits
// alongside the next TypeScript change.
const copyStaticPlugin = {
    name: "copy-static",
    setup(build) {
        build.onEnd(result => {
            if (result.errors.length === 0) copyStatic();
        });
    },
};

// ── Build options ────────────────────────────────────────────

const isDev = process.argv.includes("--dev");
const isWatch = process.argv.includes("--watch");

const options = {
    entryPoints: ["src/background.ts", "src/popup.ts"],
    bundle: true,
    format: "esm",
    target: "es2020",
    outdir: OUT_DIR,
    define,
    minify: !isDev,
    sourcemap: isDev,
    plugins: [copyStaticPlugin],
};

// ── Run ──────────────────────────────────────────────────────

rmSync(OUT_DIR, {recursive: true, force: true});
mkdirSync(OUT_DIR, {recursive: true});

if (isWatch) {
    const ctx = await esbuild.context(options);
    await ctx.watch();
    console.log("Watching for changes...");
} else {
    await esbuild.build(options);
    console.log(`Built extension to ${OUT_DIR}/ (host permissions: ${apiOrigin}/*)`);
}
