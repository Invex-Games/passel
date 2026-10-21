import {describe, expect, it} from "vitest";
import {mergePackets, packetDataForInput, parsePacket, parsePackets, toLiveEvent, tryParseHttpUrl} from "../src/packets.js";
import type {Packet} from "../src/types.js";

const text = (id: string): Packet => ({id, type: "text", title: null, payload: `body ${id}`, isImage: false});

describe("parsePacket", () => {
    it("parses each packet type from the wire format", () => {
        expect(parsePacket({$type: "Link", id: "1", name: "Example", url: "https://example.com"}))
            .toEqual({id: "1", type: "link", title: "Example", payload: "https://example.com", isImage: false});
        expect(parsePacket({$type: "Text", id: "2", title: null, body: "hello"}))
            .toEqual({id: "2", type: "text", title: null, payload: "hello", isImage: false});
        expect(parsePacket({$type: "File", id: "3", fileName: "a.png", parcelId: "p", contentType: "image/png", isImage: true}))
            .toEqual({id: "3", type: "file", title: "a.png", payload: "p", isImage: true});
    });

    it("rejects unknown types and missing required fields", () => {
        expect(parsePacket({$type: "Video", id: "1"})).toBeNull();
        expect(parsePacket({$type: "Text", id: "", body: "x"})).toBeNull();
        expect(parsePacket({$type: "Text", id: "1", body: ""})).toBeNull();
        expect(parsePacket({$type: "Link", id: "1", name: null})).toBeNull();
        expect(parsePacket("nope")).toBeNull();
        expect(parsePacket(null)).toBeNull();
    });

    it("skips packets that can't be parsed instead of failing the page", () => {
        expect(parsePackets([{$type: "Text", id: "1", body: "a"}, {$type: "Video", id: "2"}, 3]).map(p => p.id))
            .toEqual(["1"]);
        expect(parsePackets(undefined)).toEqual([]);
    });
});

describe("toLiveEvent", () => {
    it("maps stream events", () => {
        expect(toLiveEvent("resync", "")).toEqual({kind: "resync"});
        expect(toLiveEvent("packet-created", '{"$type":"Text","id":"1","title":null,"body":"hi"}'))
            .toEqual({kind: "created", packet: {id: "1", type: "text", title: null, payload: "hi", isImage: false}});
        expect(toLiveEvent("packet-deleted", '{"id":"1"}')).toEqual({kind: "deleted", id: "1"});
    });

    it("ignores heartbeats, unknown events and malformed data", () => {
        expect(toLiveEvent("ping", "")).toBeNull();
        expect(toLiveEvent("packet-moved", '{"id":"1"}')).toBeNull();
        expect(toLiveEvent("packet-created", "{not json")).toBeNull();
        expect(toLiveEvent("packet-deleted", '{"id":3}')).toBeNull();
    });
});

describe("mergePackets", () => {
    it("appends unseen packets and drops duplicates", () => {
        expect(mergePackets([text("a"), text("b")], [text("b"), text("c"), text("c")]).map(p => p.id))
            .toEqual(["a", "b", "c"]);
    });
});

describe("tryParseHttpUrl / packetDataForInput", () => {
    it.each(["https://example.com", "http://example.com/path?q=1#frag", "  https://example.com/trimmed  "])(
        "treats %j as a link", input => {
            expect(tryParseHttpUrl(input)).not.toBeNull();
            expect(packetDataForInput(input)).toEqual({$type: "Link", name: null, url: input.trim()});
        });

    it.each(["hello world", "example.com", "javascript:alert(1)", "file:///etc/passwd", "https://example.com and more", "https://"])(
        "treats %j as text", input => {
            expect(tryParseHttpUrl(input)).toBeNull();
            expect(packetDataForInput(input)).toEqual({$type: "Text", title: null, body: input.trim()});
        });
});
