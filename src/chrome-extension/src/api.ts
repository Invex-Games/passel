import {API_URL, PARCELS_URL} from "./config.js";
import {getIdToken, removeIdToken, signIn} from "./auth.js";
import type {PacketData} from "./types.js";

async function authenticatedFetch(url: string, options: RequestInit = {}): Promise<Response | null> {
    let token = await getIdToken();
    if (!token) {
        try {
            token = await signIn();
        } catch {
            console.error("Passel: user not signed in");
            return null;
        }
    }

    const headers = new Headers(options.headers);
    headers.set("Authorization", `Bearer ${token}`);
    options.headers = headers;

    const response = await fetch(url, options);

    if (response.status === 401) {
        try {
            token = await signIn();
            headers.set("Authorization", `Bearer ${token}`);
            options.headers = headers;
            return await fetch(url, options);
        } catch {
            await removeIdToken();
            return null;
        }
    }

    return response;
}

export async function sendPacket(data: PacketData): Promise<boolean> {
    const response = await authenticatedFetch(API_URL, {
        method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(data),
    });

    if (!response) return false;

    if (!response.ok) {
        console.error(`Passel: API returned ${response.status}`);
    }

    return response.ok;
}

export async function uploadParcel(file: Blob, filename?: string): Promise<string> {
    const formData = new FormData();
    formData.append("file", file, filename);

    const response = await authenticatedFetch(PARCELS_URL, {
        method: "POST", body: formData,
    });

    if (!response?.ok) {
        throw new Error(`Parcel upload failed: ${response?.status}`);
    }

    const body = await response.json();
    return body.id;
}
