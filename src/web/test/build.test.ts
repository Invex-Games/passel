import {type ChildProcess, spawn, spawnSync} from "node:child_process";
import {existsSync, mkdtempSync, readdirSync, readFileSync, rmSync} from "node:fs";
import {createServer} from "node:net";
import {tmpdir} from "node:os";
import {dirname, join} from "node:path";
import {fileURLToPath} from "node:url";
import {afterAll, beforeAll, describe, expect, it} from "vitest";
import {appCsp, PAGE_CSP} from "../policy.mjs";

// The production build as BuildWebApp runs it, so a broken build.mjs fails the tests on every pull request and not
// first in the deployment, and the dev server, which stands in for the web image locally. Each builds into a folder
// of its own, leaving dist/ (and a dev server using it) alone.

const webDir = join(dirname(fileURLToPath(import.meta.url)), "..");

const API_URL = "https://api.example.test/api/packets";
const CLIENT_ID = "build-test.apps.googleusercontent.com";

/** The environment without anything a developer's shell or the AppHost might have set for the web app. */
function cleanEnv(extra: Record<string, string>): NodeJS.ProcessEnv {
    const env = {...process.env};
    for (const key of ["API_URL", "GOOGLE_CLIENT_ID", "DEV_SIGN_IN", "PORT"]) delete env[key];
    return {...env, ...extra};
}

describe("the production build", () => {
    let outDir: string;
    let result: ReturnType<typeof spawnSync>;

    const built = (path: string) => readFileSync(join(outDir, path), "utf8");

    beforeAll(() => {
        outDir = mkdtempSync(join(tmpdir(), "passel-web-build-"));
        // No API_URL and no client id: the site doesn't depend on them. A developer's .env in src/web is still read,
        // so the checks below look for any client id at all, not just this one
        result = spawnSync(process.execPath, ["build.mjs"], {
            cwd: webDir,
            env: cleanEnv({WEB_APP_OUT_DIR: outDir}),
            encoding: "utf8",
        });
    }, 60_000);

    afterAll(() => {
        rmSync(outDir, {recursive: true, force: true});
    });

    it("succeeds without any environment's values", () => {
        expect(result.stderr).toBe("");
        expect(result.status).toBe(0);
    });

    it("writes the static pages, the app and the styles, and no source maps", () => {
        for (const path of ["index.html", "404.html", "privacy/index.html", "terms/index.html", "site.css", "app/index.html", "app/app.js", "app/styles.css"]) {
            expect(existsSync(join(outDir, path)), path).toBe(true);
        }
        expect(readdirSync(join(outDir, "app")).filter(name => name.endsWith(".map"))).toEqual([]);
    });

    it("writes no configuration: the web image or the dev server serves it", () => {
        expect(existsSync(join(outDir, "app", "config.json"))).toBe(false);
    });

    it("leaves the security and referrer policies to the server", () => {
        for (const path of ["index.html", "404.html", "privacy/index.html", "terms/index.html", "app/index.html"]) {
            const html = built(path);
            expect(html, path).not.toContain("Content-Security-Policy");
            expect(html, path).not.toMatch(/%[A-Z]+%/);
        }
        expect(built("app/index.html")).not.toContain('name="referrer"');
    });

    it("compiles in no API address, client id, development sign-in or live reload", () => {
        const app = built("app/app.js");

        expect(app).toContain("/app/config.json");
        expect(app).not.toContain("api.example.test");
        expect(app).not.toMatch(/[A-Za-z0-9-]+\.apps\.googleusercontent\.com/);
        expect(app).not.toContain("__FLAGS__");
        expect(app).not.toContain("/esbuild");
    });
});

/** A port nothing is listening on, from the operating system. */
function freePort(): Promise<number> {
    return new Promise((resolve, reject) => {
        const server = createServer();
        server.once("error", reject);
        server.listen(0, "127.0.0.1", () => {
            const address = server.address();
            server.close(() => typeof address === "object" && address ? resolve(address.port) : reject(new Error("no port")));
        });
    });
}

describe("the dev server", () => {
    let outDir: string;
    let child: ChildProcess | undefined;
    let base: string;
    let output = "";

    beforeAll(async () => {
        outDir = mkdtempSync(join(tmpdir(), "passel-web-serve-"));
        const port = await freePort();
        base = `http://localhost:${port}`;
        const server = spawn(process.execPath, ["build.mjs", "--serve"], {
            cwd: webDir,
            env: cleanEnv({WEB_APP_OUT_DIR: outDir, PORT: String(port), API_URL, GOOGLE_CLIENT_ID: CLIENT_ID}),
        });
        child = server;
        await new Promise<void>((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error(`The dev server did not start: ${output}`)), 50_000);
            const read = (chunk: Buffer) => {
                output += chunk.toString();
                if (output.includes("Serving ")) {
                    clearTimeout(timer);
                    resolve();
                }
            };
            server.stdout.on("data", read);
            server.stderr.on("data", read);
            server.once("exit", code => {
                clearTimeout(timer);
                reject(new Error(`The dev server exited (${code}): ${output}`));
            });
        });
    }, 60_000);

    afterAll(() => {
        child?.kill();
        rmSync(outDir, {recursive: true, force: true});
    });

    it("serves the configuration from the environment, revalidated every time", async () => {
        const response = await fetch(`${base}/app/config.json`);

        expect(response.status).toBe(200);
        expect(response.headers.get("content-type")).toBe("application/json");
        expect(response.headers.get("cache-control")).toBe("no-cache");
        expect(await response.json()).toEqual({API_URL, GOOGLE_CLIENT_ID: CLIENT_ID});
    });

    it("sends the app's policy, with the API's origin, under /app/", async () => {
        for (const path of ["/app/", "/app/app.js", "/app/config.json"]) {
            const response = await fetch(`${base}${path}`);
            await response.arrayBuffer();
            expect(response.headers.get("content-security-policy"), path).toBe(appCsp("https://api.example.test"));
            expect(response.headers.get("referrer-policy"), path).toBe("no-referrer-when-downgrade");
        }
        expect(await (await fetch(`${base}/app/`)).text()).toContain('src="/app/app.js"');
    });

    it("sends the no-script policy everywhere else", async () => {
        for (const path of ["/", "/privacy/", "/terms/", "/nope"]) {
            const response = await fetch(`${base}${path}`);
            await response.arrayBuffer();
            expect(response.headers.get("content-security-policy"), path).toBe(PAGE_CSP);
        }
    });
});
