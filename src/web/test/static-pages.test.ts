import {existsSync, readdirSync, readFileSync, statSync} from "node:fs";
import {dirname, join, relative} from "node:path";
import {fileURLToPath} from "node:url";
import {describe, expect, it} from "vitest";

// The static pages as they are shipped: they link to things that exist, carry no script, keep to the vocabulary of
// ADR 0002, and the legal drafts say they are drafts and quote the limits the API really enforces.

const webDir = join(dirname(fileURLToPath(import.meta.url)), "..");
const staticDir = join(webDir, "static");
const apiDir = join(webDir, "..", "webapi", "Invex.Passel.WebApi");

/** Files build.mjs writes next to the static ones. */
const BUILT_FILES = ["/site.css", "/app/app.js", "/app/styles.css"];

function htmlFiles(dir: string): string[] {
    return readdirSync(dir).flatMap(name => {
        const path = join(dir, name);
        if (statSync(path).isDirectory()) return htmlFiles(path);
        return name.endsWith(".html") ? [path] : [];
    });
}

const pages = htmlFiles(staticDir).map(path => ({
    name: relative(staticDir, path).replace(/\\/g, "/"),
    html: readFileSync(path, "utf8"),
}));

const staticPages = pages.filter(page => !page.name.startsWith("app/"));

const page = (name: string) => {
    const found = pages.find(p => p.name === name);
    if (!found) throw new Error(`No static page ${name}`);
    return found.html;
};

/** The text a reader sees: no comments, tags or attribute values. */
function visibleText(html: string): string {
    return html
        .replace(/<!--[\s\S]*?-->/g, " ")
        .replace(/<(script|style)[\s\S]*?<\/\1>/g, " ")
        .replace(/<[^>]+>/g, " ")
        .replace(/\s+/g, " ");
}

/** Whether a site path resolves to a file the build ships. */
function resolves(path: string): boolean {
    const clean = path.split(/[?#]/)[0];
    if (BUILT_FILES.includes(clean)) return true;
    const file = clean.endsWith("/") ? `${clean}index.html` : clean;
    return existsSync(join(staticDir, file));
}

describe("static pages", () => {
    it("are the landing, privacy, terms and not-found pages, and the app shell", () => {
        expect(pages.map(p => p.name).sort()).toEqual(["404.html", "app/index.html", "index.html", "privacy/index.html", "terms/index.html"]);
    });

    it.each(pages.map(p => p.name))("%s only links to pages and files that exist", name => {
        const paths = [...page(name).matchAll(/(?:href|src)="(\/[^"]*)"/g)].map(match => match[1]);

        expect(paths.length).toBeGreaterThan(0);
        expect(paths.filter(path => !resolves(path))).toEqual([]);
    });

    it.each(staticPages.map(p => p.name))("%s runs no script, under a policy that allows none", name => {
        const html = page(name);

        expect(html).not.toMatch(/<script/i);
        expect(html).toMatch(/<meta http-equiv="Content-Security-Policy" content="[^"]*script-src 'none'/);
    });

    it.each(staticPages.map(p => p.name))("%s speaks of links, notes and files, never packets or parcels", name => {
        expect(visibleText(page(name))).not.toMatch(/\b(packet|parcel)s?\b/i);
    });

    it("the app shell gets its security policy and referrer policy from the build", () => {
        const html = page("app/index.html");

        expect(html).toContain('content="%CSP%"');
        expect(html).toContain('content="%REFERRER%"');
    });
});

describe("the privacy policy and terms drafts", () => {
    const legal = ["privacy/index.html", "terms/index.html"];

    it.each(legal)("%s says it is a draft pending legal review and keeps out of search engines", name => {
        const html = page(name);

        expect(visibleText(html)).toContain("Draft, pending legal review.");
        expect(html).toContain('<meta name="robots" content="noindex">');
    });

    it.each(legal)("%s marks every fact still to be supplied as a placeholder", name => {
        const html = page(name).replace(/<!--[\s\S]*?-->/g, "");
        const bracketed = [...html.matchAll(/\[[A-Z][^\]]*\]/g)].map(match => match.index);
        const marked = [...html.matchAll(/<span class="placeholder">\s*\[[A-Z][^\]]*\]\s*<\/span>/g)].length;

        expect(bracketed.length).toBeGreaterThan(0);
        expect(marked).toBe(bracketed.length);
    });

    /** A default from the API's options, as the C# source declares it. */
    function apiDefault(file: string, property: string): string {
        const source = readFileSync(join(apiDir, file), "utf8");
        const match = new RegExp(`${property} \\{ get; set; \\} = ([^;]+);`).exec(source);
        if (!match) throw new Error(`No default for ${property} in ${file}`);
        return match[1];
    }

    it("quote the quotas and rate limits the API enforces by default", () => {
        const privacy = visibleText(page("privacy/index.html"));
        const terms = visibleText(page("terms/index.html"));

        expect(apiDefault("Options/QuotaOptions.cs", "MaxPacketsPerUser")).toBe("5000");
        expect(apiDefault("Options/QuotaOptions.cs", "MaxBytesPerUser")).toBe("50 * 1024 * 1024");
        for (const text of [privacy, terms]) expect(text).toContain("5,000 items and 50 MB");

        expect(apiDefault("Options/RateLimitOptions.cs", "CreatesPerMinute")).toBe("30");
        expect(apiDefault("Options/RateLimitOptions.cs", "ReadsPerMinute")).toBe("120");
        expect(apiDefault("Options/RateLimitOptions.cs", "StreamsPerMinute")).toBe("10");
        expect(terms).toContain("30 items sent, 120 other requests and 10 live connections opened per minute");
    });

    it("describe the preview fetcher as it is configured and introduces itself", () => {
        const privacy = visibleText(page("privacy/index.html"));
        const fetcher = readFileSync(join(apiDir, "Services/LinkPreviewFetcher.cs"), "utf8");

        expect(apiDefault("Options/LinkPreviewOptions.cs", "MaxBodyBytes")).toBe("512 * 1024");
        expect(apiDefault("Options/LinkPreviewOptions.cs", "MaxRedirects")).toBe("3");
        expect(apiDefault("Options/LinkPreviewOptions.cs", "MaxAttempts")).toBe("3");
        expect(privacy).toContain("follows at most three redirects, reads at most 512 KB, and gives up after three attempts");

        const userAgent = /UserAgent = "([^"]+)"/.exec(fetcher)?.[1];
        expect(userAgent).toBeDefined();
        expect(privacy).toContain(userAgent!);
    });
});
