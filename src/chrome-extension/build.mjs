import * as esbuild from "esbuild";
import {readFileSync, existsSync} from "fs";

// ── Load environment variables ───────────────────────────────
// Reads from .env file if present (local dev), otherwise uses process.env (CI)

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
            const value = trimmed.slice(eqIndex + 1).trim();
            env[key] = value;
        }
    }

    return env;
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

const define = {
    "__CONFIG__.API_URL": JSON.stringify(required("API_URL")),
    "__CONFIG__.PARCELS_URL": JSON.stringify(required("PARCELS_URL")),
    "__CONFIG__.GOOGLE_CLIENT_ID": JSON.stringify(required("GOOGLE_CLIENT_ID")),
};

// ── Build options ────────────────────────────────────────────

const isDev = process.argv.includes("--dev");
const isWatch = process.argv.includes("--watch");

const options = {
    entryPoints: ["src/background.ts", "src/popup.ts"],
    bundle: true,
    format: "esm",
    target: "es2020",
    outdir: "dist",
    define,
    minify: !isDev,
    sourcemap: isDev,
};

// ── Run ──────────────────────────────────────────────────────

if (isWatch) {
    const ctx = await esbuild.context(options);
    await ctx.watch();
    console.log("Watching for changes...");
} else {
    await esbuild.build(options);
}

