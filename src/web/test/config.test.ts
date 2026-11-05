import {describe, expect, it} from "vitest";
import {CONFIG_PATH, ConfigError, loadConfig, parseConfig} from "../src/app/config.js";

// The configuration the app reads at startup from /app/config.json: what it accepts, and that anything else stops it
// with a reason instead of starting against the wrong API.

const CLIENT_ID = "123-abc.apps.googleusercontent.com";
const API_URL = "https://api.passel.link/api/packets";
const production = {devSignIn: false};
const development = {devSignIn: true};

const config = (patch: Record<string, unknown> = {}) => ({API_URL, GOOGLE_CLIENT_ID: CLIENT_ID, ...patch});

describe("parseConfig", () => {
    it("accepts an https API and a Google client id", () => {
        expect(parseConfig(config(), production)).toEqual({apiUrl: API_URL, googleClientId: CLIENT_ID});
    });

    it("ignores values it does not know", () => {
        expect(parseConfig(config({EXTRA: 1}), production).apiUrl).toBe(API_URL);
    });

    it("drops a trailing slash from the API URL", () => {
        expect(parseConfig(config({API_URL: `${API_URL}/`}), production).apiUrl).toBe(API_URL);
    });

    it.each(["http://localhost:5218/api/packets", "http://127.0.0.1:5218/api/packets", "http://[::1]:5218/api/packets"])(
        "allows plain http to this machine only (%s)", apiUrl => {
            expect(parseConfig(config({API_URL: apiUrl}), production).apiUrl).toBe(apiUrl);
        });

    it.each([
        ["missing", undefined, "API_URL is missing"],
        ["empty", "", "API_URL is missing"],
        ["not a string", 42, "API_URL is missing"],
        ["not a URL", "api.passel.link", "API_URL is not a URL"],
        ["plain http to another host", "http://api.passel.link/api/packets", "must be an https URL"],
        ["plain http to a look-alike", "http://localhost.example.com/api/packets", "must be an https URL"],
        ["another scheme", "javascript:alert(1)", "must be an https URL"],
        ["with credentials", "https://user:pw@api.passel.link/api/packets", "no credentials, query or fragment"],
        ["with a query", "https://api.passel.link/api/packets?x=1", "no credentials, query or fragment"],
        ["with a fragment", "https://api.passel.link/api/packets#x", "no credentials, query or fragment"],
    ])("refuses an API URL that is %s", (_, apiUrl, message) => {
        expect(() => parseConfig(config({API_URL: apiUrl}), production)).toThrow(ConfigError);
        expect(() => parseConfig(config({API_URL: apiUrl}), production)).toThrow(message);
    });

    it.each([
        ["missing", undefined, "GOOGLE_CLIENT_ID is missing"],
        ["empty", "", "GOOGLE_CLIENT_ID is missing"],
        ["not a string", 7, "GOOGLE_CLIENT_ID is not a string"],
        ["a placeholder", "invex-passel-server", "is not a Google OAuth client id"],
        ["the bare suffix", ".apps.googleusercontent.com", "is not a Google OAuth client id"],
        ["another domain", "123-abc.apps.googleusercontent.com.example.com", "is not a Google OAuth client id"],
        ["with a quote", "123\"abc.apps.googleusercontent.com", "is not a Google OAuth client id"],
    ])("refuses a client id that is %s", (_, clientId, message) => {
        expect(() => parseConfig(config({GOOGLE_CLIENT_ID: clientId}), production)).toThrow(ConfigError);
        expect(() => parseConfig(config({GOOGLE_CLIENT_ID: clientId}), production)).toThrow(message);
    });

    it("lets a build with the development sign-in do without a client id, but not with a bad one", () => {
        expect(parseConfig(config({GOOGLE_CLIENT_ID: ""}), development).googleClientId).toBe("");
        expect(parseConfig({API_URL}, development).googleClientId).toBe("");
        expect(() => parseConfig(config({GOOGLE_CLIENT_ID: "nope"}), development)).toThrow(ConfigError);
    });

    it.each([null, [], "config", 1])("refuses a configuration that is not an object (%j)", raw => {
        expect(() => parseConfig(raw, production)).toThrow("not a JSON object");
    });
});

describe("loadConfig", () => {
    const respond = (body: string, status = 200) =>
        (async () => new Response(body, {status, headers: {"Content-Type": "application/json"}})) as unknown as typeof fetch;

    it("fetches the configuration from the app's own origin, revalidating it", async () => {
        const calls: [string, RequestInit | undefined][] = [];
        const fetchConfig = (async (url: string, init?: RequestInit) => {
            calls.push([url, init]);
            return new Response(JSON.stringify(config()));
        }) as unknown as typeof fetch;

        expect(await loadConfig(fetchConfig, production)).toEqual({apiUrl: API_URL, googleClientId: CLIENT_ID});
        expect(calls).toEqual([[CONFIG_PATH, {cache: "no-cache", credentials: "same-origin"}]]);
        expect(CONFIG_PATH).toBe("/app/config.json");
    });

    it("says when the server has no configuration", async () => {
        await expect(loadConfig(respond("Not found", 404), production)).rejects.toThrow("/app/config.json answered 404");
    });

    it("says when the configuration is not JSON", async () => {
        await expect(loadConfig(respond("<!DOCTYPE html>"), production)).rejects.toThrow("/app/config.json is not JSON");
    });

    it("says when the server can't be reached", async () => {
        const offline = (async () => {
            throw new TypeError("Failed to fetch");
        }) as unknown as typeof fetch;

        await expect(loadConfig(offline, production)).rejects.toThrow(ConfigError);
        await expect(loadConfig(offline, production)).rejects.toThrow("could not be fetched (Failed to fetch)");
    });

    it("checks what it fetched", async () => {
        await expect(loadConfig(respond(JSON.stringify(config({API_URL: "http://api.passel.link"}))), production))
            .rejects.toThrow("must be an https URL");
    });
});
