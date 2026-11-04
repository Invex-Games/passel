import type {LinkPreview, LiveEvent, Packet, PacketData} from "./types.js";

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
        case "Link": {
            if (typeof json.url !== "string" || !json.url) return null;

            const link: Packet = {id, type: "link", title: optionalString(json.name), payload: json.url, isImage: false};
            const preview = parsePreview(json.preview);
            return preview ? {...link, preview} : link;
        }

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

/**
 * Parses a link's `preview`, or returns null when there is none or nothing in it can be shown. Blank strings count
 * as missing, and an image URL that isn't an absolute http(s) URL is dropped: a client puts it in an `<img>`.
 */
export function parsePreview(raw: unknown): LinkPreview | null {
    if (typeof raw !== "object" || raw === null) return null;
    const json = raw as Record<string, unknown>;

    const title = nonBlank(json.title);
    const description = nonBlank(json.description);
    const image = nonBlank(json.imageUrl);
    const imageUrl = image && tryParseHttpUrl(image) ? image : null;

    return title || description || imageUrl ? {title, description, imageUrl} : null;
}

function nonBlank(value: unknown): string | null {
    return typeof value === "string" && value.trim() ? value : null;
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

/** The longest link name (and text title) the API accepts; longer ones are rejected. */
export const MAX_TITLE_LENGTH = 256;

/**
 * Cuts a page title down to what the API accepts as a link name. A tab's title is whatever the page set,
 * and sending one over the limit would fail the whole send.
 */
export function truncateTitle(title: string, maxLength = MAX_TITLE_LENGTH): string {
    if (title.length <= maxLength) return title;

    // Don't cut a surrogate pair in half: a lone surrogate isn't valid in the JSON the API reads.
    const end = /[\uD800-\uDBFF]/.test(title[maxLength - 1]) ? maxLength - 1 : maxLength;
    return title.slice(0, end);
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

        case "packet-updated": {
            const packet = parsePacket(parseJson(data));
            return packet ? {kind: "updated", packet} : null;
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
