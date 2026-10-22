import {describe, expect, it} from "vitest";
import {MAX_BUFFER_LENGTH, ServerSentEventParser} from "../src/sse.js";

/** Feeds `chunks` to a fresh parser and returns the events as [event, data] pairs. */
function decode(...chunks: string[]): [string, string][] {
    const parser = new ServerSentEventParser();
    return chunks.flatMap(chunk => parser.push(chunk)).map(e => [e.event, e.data]);
}

describe("ServerSentEventParser", () => {
    it("decodes event types and data", () => {
        expect(decode("event: resync\ndata: \n\nevent: packet-created\ndata: {\"id\":\"1\"}\n\n")).toEqual([
            ["resync", ""],
            ["packet-created", "{\"id\":\"1\"}"],
        ]);
    });

    it("joins multi-line data and defaults the type to message", () => {
        expect(decode("data: first\ndata: second\n\n")).toEqual([["message", "first\nsecond"]]);
    });

    it("ignores comments, unknown fields and events without data", () => {
        expect(decode(": keep-alive\n\n", "event: ping\n\n", "id: 7\nretry: 1000\nevent: resync\ndata: \n\n"))
            .toEqual([["resync", ""]]);
    });

    it("handles CRLF and CR line endings, and fields without a space", () => {
        expect(decode("event:resync\r\ndata:x\r\n\r\n")).toEqual([["resync", "x"]]);
        // A trailing "\r" could be half of a "\r\n", so the last event completes once more data arrives.
        expect(decode("data: a\r\rdata: b\r\r", ": ping")).toEqual([["message", "a"], ["message", "b"]]);
    });

    it("reassembles lines split across chunks, including a split CRLF", () => {
        expect(decode("ev", "ent: packet-cre", "ated\ndata: 1\r", "\n\r", "\n")).toEqual([["packet-created", "1"]]);
    });

    it("waits for the blank line before emitting an event", () => {
        const parser = new ServerSentEventParser();
        expect(parser.push("event: resync\ndata: \n")).toEqual([]);
        expect(parser.push("\n")).toEqual([{event: "resync", data: ""}]);
    });
});

describe("ServerSentEventParser buffer limit", () => {
    const half = "x".repeat(MAX_BUFFER_LENGTH / 2);

    it("gives up on a line that grows past the limit", () => {
        const parser = new ServerSentEventParser();
        parser.push(half);
        // Exactly the limit is still fine.
        expect(parser.push(half)).toEqual([]);
        expect(() => parser.push("x")).toThrow(/longer than/);
    });

    it("counts the data lines of an unfinished event towards the limit", () => {
        const parser = new ServerSentEventParser();
        parser.push(`data: ${half}\n`);
        expect(() => parser.push(`data: ${half}x\n`)).toThrow(/longer than/);
    });

    it("starts counting afresh once an event completes", () => {
        const parser = new ServerSentEventParser();
        expect(parser.push(`data: ${half}\n\n`)).toHaveLength(1);
        expect(parser.push(`data: ${half}x\n\n`)).toHaveLength(1);
    });
});
