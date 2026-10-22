// Serves the built popup (dist/) with chrome.* stubbed, so it can be looked at and clicked through in any
// browser without loading the extension: `npm run harness`, then open http://127.0.0.1:8765/.
//
// Query parameters: ?state=signedout shows the signed-out popup, ?state=empty an empty feed, and
// &lastDevUser=alice offers that development user in the signed-out form. Build first (`npm run dev`).
import {createServer} from "node:http";
import {readFile} from "node:fs/promises";
import {dirname, extname, join, normalize} from "node:path";
import {fileURLToPath} from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const dist = join(here, "..", "dist");
const port = Number(process.argv[2] ?? 8765);

const types = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript",
    ".css": "text/css",
    ".png": "image/png",
    ".svg": "image/svg+xml",
    ".map": "application/json",
    ".json": "application/json",
};

// The stub runs before popup.js, and the body is framed like the real 380 by 600 popup.
const injected = `
    <style>
        html { background: #888; }
        body { margin: 24px auto; box-shadow: 0 8px 32px rgba(0, 0, 0, .45); height: 600px; }
    </style>
    <script src="chrome-stub.js"></script>
`;

async function page() {
    const html = await readFile(join(dist, "popup.html"), "utf8");
    return html.replace("</head>", `${injected}</head>`);
}

createServer(async (req, res) => {
    const path = normalize(decodeURIComponent(new URL(req.url, "http://x").pathname)).replace(/^([/\\])+/, "");
    try {
        if (!path || path === "popup.html") {
            res.writeHead(200, {"Content-Type": types[".html"]});
            res.end(await page());
            return;
        }
        const file = path === "chrome-stub.js" ? join(here, "chrome-stub.js") : join(dist, path);
        // Read before answering, so a missing file (a favicon, a stale source map) still gets its 404
        const body = await readFile(file);
        res.writeHead(200, {"Content-Type": types[extname(path)] ?? "application/octet-stream"});
        res.end(body);
    } catch {
        res.writeHead(404);
        res.end("not found");
    }
}).listen(port, "127.0.0.1", () => console.log(`Popup harness at http://127.0.0.1:${port}/ (serving ${dist})`));
