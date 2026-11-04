import type {FeedState, Packet} from "@passel/core";
import {describe, expect, it} from "vitest";
import {
    EMPTY_FEED_MESSAGE,
    END_OF_FEED_MESSAGE,
    feedEnd,
    isLongNote,
    linkHost,
    linkHref,
    packetTitle,
    syncLabel,
} from "../src/app/labels.js";

const packet = (patch: Partial<Packet>): Packet => ({id: "1", type: "text", title: null, payload: "hi", isImage: false, ...patch});

const feed = (patch: Partial<FeedState>): FeedState => ({
    packets: [], nextCursor: null, lastSyncedAt: null, loaded: true, loadingMore: false, loadMoreError: null, ...patch,
});

describe("packet labels", () => {
    it("titles an item by its own title, or by what it is in the words of ADR 0002", () => {
        expect(packetTitle(packet({title: "Shopping"}))).toBe("Shopping");
        expect(packetTitle(packet({type: "text"}))).toBe("Note");
        expect(packetTitle(packet({type: "link", payload: "https://example.com"}))).toBe("Link");
        expect(packetTitle(packet({type: "file", payload: "p1"}))).toBe("File");
    });

    it("only makes http(s) links clickable", () => {
        expect(linkHref(packet({type: "link", payload: "https://example.com/a b"}))).toBeNull();
        expect(linkHref(packet({type: "link", payload: "https://example.com/page"}))).toBe("https://example.com/page");
        expect(linkHref(packet({type: "link", payload: "javascript:alert(1)"}))).toBeNull();
        expect(linkHref(packet({type: "text", payload: "https://example.com"}))).toBeNull();
    });

    it("shows a link's host without www", () => {
        expect(linkHost("https://www.example.com/page")).toBe("example.com");
        expect(linkHost("http://docs.example.com:8080/")).toBe("docs.example.com");
    });

    it("collapses notes that run past eight lines or 600 characters", () => {
        expect(isLongNote("short")).toBe(false);
        expect(isLongNote(Array(8).fill("line").join("\n"))).toBe(false);
        expect(isLongNote(Array(9).fill("line").join("\n"))).toBe(true);
        expect(isLongNote("x".repeat(601))).toBe(true);
    });
});

describe("syncLabel", () => {
    const now = 10_000_000;

    it("says Live while the stream is open, otherwise how long ago the feed was fetched", () => {
        expect(syncLabel(true, null, now)).toBe("Live");
        expect(syncLabel(false, null, now)).toBeNull();
        expect(syncLabel(false, now - 59_000, now)).toBe("Synced just now");
        expect(syncLabel(false, now - 60_000, now)).toBe("Synced 1 min ago");
        expect(syncLabel(false, now - 5 * 60_000, now)).toBe("Synced 5 mins ago");
        expect(syncLabel(false, now - 3 * 3600_000, now)).toBe("Synced 3 hours ago");
        expect(syncLabel(false, now - 49 * 3600_000, now)).toBe("Synced 2 days ago");
    });
});

describe("feedEnd", () => {
    it("shows a spinner while the first page loads, or why it failed", () => {
        expect(feedEnd(feed({loaded: false}), null)).toEqual({kind: "loading"});
        expect(feedEnd(feed({loaded: false}), "Offline.")).toEqual({kind: "error", message: "Offline.", retry: "refresh"});
    });

    it("shows a spinner while there are older items, or a retry once loading them failed", () => {
        expect(feedEnd(feed({nextCursor: "c1"}), null)).toEqual({kind: "loading"});
        expect(feedEnd(feed({nextCursor: "c1", loadMoreError: "Nope."}), null))
            .toEqual({kind: "error", message: "Couldn't load older items.", retry: "loadMore"});
        expect(feedEnd(feed({nextCursor: "c1", loadMoreError: "Nope.", loadingMore: true}), null)).toEqual({kind: "loading"});
    });

    it("says when there is nothing yet, and when everything is shown", () => {
        expect(feedEnd(feed({}), null)).toEqual({kind: "message", message: EMPTY_FEED_MESSAGE});
        expect(feedEnd(feed({packets: [packet({})]}), null)).toEqual({kind: "message", message: END_OF_FEED_MESSAGE});
    });
});
