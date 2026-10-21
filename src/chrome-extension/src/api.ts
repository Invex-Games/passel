import {API_URL} from "./config.js";
import {getToken, refreshToken, signOut} from "./auth.js";
import type {PacketData, ProblemDetails, SendPacketResponse} from "./types.js";

class SignedOutError extends Error {
}

/**
 * Fetches with the user's ID token. On a 401 the token is refreshed (silently first) and the
 * request retried once; if that still fails the user is signed out. `interactive` allows a
 * sign-in window to be shown and should only be true for user-initiated actions.
 */
async function authenticatedFetch(url: string, options: RequestInit, interactive: boolean): Promise<Response> {
    let token = await getToken(interactive);
    if (!token) throw new SignedOutError("You're not signed in.");

    let response = await fetchWithToken(url, options, token);
    if (response.status !== 401) return response;

    token = await refreshToken(interactive);
    if (token) {
        response = await fetchWithToken(url, options, token);
        if (response.status !== 401) return response;
    }

    await signOut();
    throw new SignedOutError("Your session has expired. Please sign in again.");
}

function fetchWithToken(url: string, options: RequestInit, token: string): Promise<Response> {
    const headers = new Headers(options.headers);
    headers.set("Authorization", `Bearer ${token}`);
    return fetch(url, {...options, headers});
}

export async function sendPacket(data: PacketData, interactive: boolean): Promise<SendPacketResponse> {
    try {
        const response = await authenticatedFetch(API_URL, {
            method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(data),
        }, interactive);

        if (response.ok) return {success: true};

        const error = await readError(response);
        console.error(`Passel: API returned ${response.status}: ${error}`);
        return {success: false, error};
    } catch (err) {
        if (err instanceof SignedOutError) return {success: false, error: err.message, signedOut: true};

        console.error("Passel: request failed", err);
        return {success: false, error: "Couldn't reach Passel. Check your connection and try again."};
    }
}

/** Builds a user-facing message from a ProblemDetails body, falling back to the status code. */
async function readError(response: Response): Promise<string> {
    const fallback = `Request failed (${response.status}).`;

    try {
        const contentType = response.headers.get("Content-Type") ?? "";
        if (!contentType.includes("json")) return fallback;

        const problem = await response.json() as ProblemDetails;
        const validationErrors = problem.errors ? Object.values(problem.errors).flat().join(" ") : "";
        const title = problem.title?.trim();
        const detail = (problem.detail || validationErrors).trim();

        if (title && detail) return /[.!?]$/.test(title) ? `${title} ${detail}` : `${title}: ${detail}`;
        return title || detail || fallback;
    } catch {
        return fallback;
    }
}
