// The app's configuration, which the built site does not contain: it is read at startup from /app/config.json on
// the app's own origin. The web image writes that file when its container starts (deploy/web/entrypoint.sh), and
// the dev server answers it from .env (build.mjs). See docs/components/web-app.md.

/** Where the configuration is served, on the app's own origin. */
export const CONFIG_PATH = "/app/config.json";

const CLIENT_ID_PATTERN = /^[A-Za-z0-9._-]+\.apps\.googleusercontent\.com$/;

/** Hosts the API may be reached at over plain http: this machine only. */
const LOOPBACK_HOSTS = new Set(["localhost", "127.0.0.1", "[::1]"]);

export interface AppConfig {
    /** The packets endpoint, not the API root. */
    apiUrl: string;
    /** The Google OAuth web client id; empty only in a development build with DEV_SIGN_IN. */
    googleClientId: string;
}

/** Why the app can't start: the configuration is missing, unreadable or not valid. */
export class ConfigError extends Error {
    override name = "ConfigError";
}

/**
 * Checks the configuration as the server sent it. The API URL must be an https URL (http only to this machine) with
 * no credentials, query or fragment; the client id must be a Google OAuth client id, and may be left empty only by a
 * build that offers the development sign-in.
 */
export function parseConfig(raw: unknown, options: { devSignIn: boolean }): AppConfig {
    if (typeof raw !== "object" || raw === null || Array.isArray(raw)) throw new ConfigError("the configuration is not a JSON object");
    const values = raw as Record<string, unknown>;

    const apiUrl = values.API_URL;
    if (typeof apiUrl !== "string" || apiUrl === "") throw new ConfigError("API_URL is missing");

    let url: URL;
    try {
        url = new URL(apiUrl);
    } catch {
        throw new ConfigError(`API_URL is not a URL (${apiUrl})`);
    }
    const secure = url.protocol === "https:" || (url.protocol === "http:" && LOOPBACK_HOSTS.has(url.hostname));
    if (!secure) throw new ConfigError(`API_URL must be an https URL (${apiUrl})`);
    if (url.username || url.password || url.search || url.hash) {
        throw new ConfigError(`API_URL must have no credentials, query or fragment (${apiUrl})`);
    }

    const clientId = values.GOOGLE_CLIENT_ID ?? "";
    if (typeof clientId !== "string") throw new ConfigError("GOOGLE_CLIENT_ID is not a string");
    if (clientId === "" && !options.devSignIn) throw new ConfigError("GOOGLE_CLIENT_ID is missing");
    if (clientId !== "" && !CLIENT_ID_PATTERN.test(clientId)) {
        throw new ConfigError(`GOOGLE_CLIENT_ID is not a Google OAuth client id (${clientId})`);
    }

    return {apiUrl: url.href.replace(/\/$/, ""), googleClientId: clientId};
}

/** Fetches and checks the configuration; any failure is a {@link ConfigError}. */
export async function loadConfig(fetchConfig: typeof fetch, options: { devSignIn: boolean }): Promise<AppConfig> {
    let response: Response;
    try {
        // Revalidated every time: the file changes whenever the container is started with other values
        response = await fetchConfig(CONFIG_PATH, {cache: "no-cache", credentials: "same-origin"});
    } catch (err) {
        throw new ConfigError(`${CONFIG_PATH} could not be fetched (${(err as Error).message})`);
    }
    if (!response.ok) throw new ConfigError(`${CONFIG_PATH} answered ${response.status}`);

    let raw: unknown;
    try {
        raw = await response.json();
    } catch {
        throw new ConfigError(`${CONFIG_PATH} is not JSON`);
    }
    return parseConfig(raw, options);
}
