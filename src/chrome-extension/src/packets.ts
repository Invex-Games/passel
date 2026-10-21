import type {LiveEvent, Packet, PacketData} from "./types.js";

/**
 * Parses a packet from the API's wire format, e.g. `{"$type": "Text", "id": "…", "title": "…", "body": "…"}`.
 * Returns null for unknown types or missing required fields, so one bad packet doesn't break the feed.
 */
export function parsePacket(raw: unknown): Packet | null {
    if (typeof raw !== "object" || raw === null) return null;
    const json = raw as Record<string, unknown>;

    const id = typeof json.id === "string" ? json.id : "";
    if (!id) return null;

    switch (json.$type) {
        case "Link":
            return typeof json.url === "string" && json.url
                ? {id, type: "link", title: optionalString(json.name), payload: json.url, isImage: false}
                : null;

        case "Text":
            return typeof json.body === "string" && json.body
                ? {id, type: "text", title: optionalString(json.title), payload: json.body, isImage: false}
                : null;

        case "File":
            // File packets are display-only, so only the id is required.
            return {
                id,
                type: "file",
                title: optionalString(json.fileName),
                payload: optionalString(json.parcelId) ?? "",
                isImage: json.isImage === true,
            };

        default:
            return null;
    }
}

/** Parses a list of wire-format packets, skipping any that can't be parsed. */
export function parsePackets(raw: unknown): Packet[] {
    if (!Array.isArray(raw)) return [];
    return raw.map(parsePacket).filter((packet): packet is Packet => packet !== null);
}

function optionalString(value: unknown): string | null {
    return typeof value === "string" ? value : null;
}

/** Returns `existing` followed by the packets of `incoming` whose ids aren't already present. */
export function mergePackets(existing: readonly Packet[], incoming: readonly Packet[]): Packet[] {
    const seen = new Set<string>();
    return [...existing, ...incoming].filter(packet => {
        if (seen.has(packet.id)) return false;
        seen.add(packet.id);
        return true;
    });
}

/**
 * Parses `text` as an absolute http(s) URL with a host. Returns null for anything else (other schemes such
 * as `javascript:`, relative paths, text containing whitespace), so only web links are treated as links.
 */
export function tryParseHttpUrl(text: string): URL | null {
    const trimmed = text.trim();
    if (!trimmed || /\s/.test(trimmed)) return null;

    try {
        const url = new URL(trimmed);
        return (url.protocol === "http:" || url.protocol === "https:") && url.hostname ? url : null;
    } catch {
        return null;
    }
}

/** Builds the packet to send for text typed into the popup: a link if it's a web URL, otherwise text. */
export function packetDataForInput(text: string): PacketData {
    const trimmed = text.trim();
    return tryParseHttpUrl(trimmed)
        ? {$type: "Link", name: null, url: trimmed}
        : {$type: "Text", title: null, body: trimmed};
}

/** Maps a server-sent event to a LiveEvent, or null for heartbeats and events this version doesn't understand. */
export function toLiveEvent(type: string, data: string): LiveEvent | null {
    switch (type) {
        case "resync":
            return {kind: "resync"};

        case "packet-created": {
            const packet = parsePacket(parseJson(data));
            return packet ? {kind: "created", packet} : null;
        }

        case "packet-deleted": {
            const id = (parseJson(data) as { id?: unknown } | null)?.id;
            return typeof id === "string" && id ? {kind: "deleted", id} : null;
        }

        default:
            return null;
    }
}

function parseJson(text: string): unknown {
    try {
        return JSON.parse(text);
    } catch {
        return null;
    }
}
