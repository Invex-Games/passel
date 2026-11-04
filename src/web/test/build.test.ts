import {spawnSync} from "node:child_process";
import {existsSync, mkdtempSync, readdirSync, readFileSync, rmSync} from "node:fs";
import {tmpdir} from "node:os";
import {dirname, join} from "node:path";
import {fileURLToPath} from "node:url";
import {afterAll, beforeAll, describe, expect, it} from "vitest";

// The production build as BuildWebApp runs it, so a broken build.mjs fails the tests on every pull request and not
// first in the deployment. It builds into a folder of its own, leaving dist/ (and a dev server using it) alone.

const webDir = join(dirname(fileURLToPath(import.meta.url)), "..");

const API_URL = "https://api.example.test/api/packets";
const CLIENT_ID = "build-test.apps.googleusercontent.com";

let outDir: string;
let result: ReturnType<typeof spawnSync>;

const built = (path: string) => readFileSync(join(outDir, path), "utf8");

beforeAll(() => {
    outDir = mkdtempSync(join(tmpdir(), "passel-web-build-"));
    result = spawnSync(process.execPath, ["build.mjs"], {
        cwd: webDir,
        env: {...process.env, API_URL, GOOGLE_CLIENT_ID: CLIENT_ID, WEB_APP_OUT_DIR: outDir},
        encoding: "utf8",
    });
}, 60_000);

afterAll(() => {
    rmSync(outDir, {recursive: true, force: true});
});

describe("the production build", () => {
    it("succeeds", () => {
        expect(result.stderr).toBe("");
        expect(result.status).toBe(0);
    });

    it("writes the static pages, the app and the styles, and no source maps", () => {
        for (const path of ["index.html", "404.html", "privacy/index.html", "terms/index.html", "site.css", "app/index.html", "app/app.js", "app/styles.css"]) {
            expect(existsSync(join(outDir, path)), path).toBe(true);
        }
        expect(readdirSync(join(outDir, "app")).filter(name => name.endsWith(".map"))).toEqual([]);
    });

    it("fills in the app's security policy and referrer policy", () => {
        const html = built("app/index.html");

        expect(html).not.toMatch(/%[A-Z]+%/);
        expect(html).toContain("connect-src 'self' https://api.example.test https://accounts.google.com/gsi/");
        expect(html).toContain('<meta name="referrer" content="strict-origin-when-cross-origin">');
    });

    it("compiles in the API URL and the client id, and no live reload", () => {
        const app = built("app/app.js");

        expect(app).toContain(API_URL);
        expect(app).toContain(CLIENT_ID);
        expect(app).not.toContain("__CONFIG__");
        expect(app).not.toContain("/esbuild");
    });
});
