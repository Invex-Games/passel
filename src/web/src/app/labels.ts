import {tryParseHttpUrl, type FeedState, type Packet} from "@passel/core";

// What the feed says, worked out apart from the views so the wording and its rules are unit-tested. The words follow
// ADR 0002: one verb, "Send", and the nouns link, note and file; "packet" stays a code and API term.

/** What the UI calls a packet: a link, a note or a file. */
export function packetNoun(packet: Packet): string {
    switch (packet.type) {
        case "link":
            return "Link";
        case "text":
            return "Note";
        case "file":
            return "File";
    }
}

export function packetTitle(packet: Packet): string {
    return packet.title || packetNoun(packet);
}

/** The link to open for a link packet, or null when its URL isn't an http(s) URL (it is then shown as text). */
export function linkHref(packet: Packet): string | null {
    return packet.type === "link" ? tryParseHttpUrl(packet.payload)?.href ?? null : null;
}

/** The host a link points at, without a leading "www.", shown under its preview. */
export function linkHost(href: string): string {
    return new URL(href).hostname.replace(/^www\./, "");
}

/** Notes longer than this many lines or characters start collapsed, with a "Show more" button. */
export const NOTE_PREVIEW_LINES = 8;
export const NOTE_PREVIEW_CHARACTERS = 600;

export function isLongNote(text: string): boolean {
    return text.length > NOTE_PREVIEW_CHARACTERS || text.split("\n").length > NOTE_PREVIEW_LINES;
}

/** "Live" while the stream is open, otherwise when the feed was last fetched ("Synced 5 mins ago"). */
export function syncLabel(live: boolean, lastSyncedAt: number | null, now: number = Date.now()): string | null {
    if (live) return "Live";
    if (lastSyncedAt === null) return null;

    const seconds = Math.floor((now - lastSyncedAt) / 1000);
    if (seconds < 60) return "Synced just now";

    const plural = (n: number, unit: string) => `Synced ${n} ${unit}${n === 1 ? "" : "s"} ago`;
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return plural(minutes, "min");
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return plural(hours, "hour");
    return plural(Math.floor(hours / 24), "day");
}

/** The row after the oldest item. */
export type FeedEnd =
    | { kind: "loading" }
    | { kind: "error"; message: string; retry: "refresh" | "loadMore" }
    | { kind: "message"; message: string };

export const EMPTY_FEED_MESSAGE =
    "Nothing here yet. Send a link or a note from the Passel extension or app, and it shows up here straight away.";

export const END_OF_FEED_MESSAGE = "That's everything.";

export function feedEnd(feed: FeedState, loadError: string | null): FeedEnd {
    if (!feed.loaded) {
        return loadError ? {kind: "error", message: loadError, retry: "refresh"} : {kind: "loading"};
    }

    if (feed.nextCursor) {
        return feed.loadMoreError && !feed.loadingMore
            ? {kind: "error", message: "Couldn't load older items.", retry: "loadMore"}
            : {kind: "loading"};
    }

    return {kind: "message", message: feed.packets.length === 0 ? EMPTY_FEED_MESSAGE : END_OF_FEED_MESSAGE};
}
