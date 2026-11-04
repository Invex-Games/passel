import {defineConfig} from "vitest/config";

// The defaults run the tests; the coverage settings are for `npm run test:coverage`, which `dotnet atom
// TestChromeExtension` runs and keeps the lcov report of.
export default defineConfig({
    test: {
        coverage: {
            provider: "v8",
            include: ["src/**/*.{ts,tsx}"],
            reporter: ["text-summary", "lcov"],
            reportsDirectory: "coverage",
        },
    },
});
