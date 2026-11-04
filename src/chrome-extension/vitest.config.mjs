import {defineConfig} from "vitest/config";

// The service worker's modules are tested in Node, which, like a service worker, has fetch and streams but no
// DOM; the popup's tests ask for jsdom in their first line. Every test gets a stubbed `chrome` from
// test/chrome.ts, and the build-time __CONFIG__ from test/setup.ts.
export default defineConfig({
    test: {
        include: ["test/**/*.test.ts"],
        setupFiles: ["test/setup.ts"],
        mockReset: true,
        restoreMocks: true,
        unstubGlobals: true,
        coverage: {
            provider: "v8",
            include: ["src/**/*.ts"],
            reporter: ["text-summary", "lcov"],
            reportsDirectory: "coverage",
        },
    },
});
