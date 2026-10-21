export interface ServerSentEvent {
    /** The `event:` field, or "message" when the server didn't send one. */
    event: string;
    /** The `data:` lines, joined with "\n". */
    data: string;
}

/**
 * Incrementally decodes a `text/event-stream` body, following the parsing rules of the HTML
 * Server-Sent Events spec: comments (`:` lines) and unknown fields are ignored, and an event
 * without `data:` is not emitted. Feed it decoded text in chunks of any size.
 */
export class ServerSentEventParser {
    private buffer = "";
    private event = "";
    private data: string[] = [];

    /** Adds a chunk of the body and returns the events it completed. */
    push(chunk: string): ServerSentEvent[] {
        this.buffer += chunk;
        const events: ServerSentEvent[] = [];

        // A trailing "\r" may be the first half of a "\r\n" split across chunks, so it waits for the next one.
        let match: RegExpExecArray | null;
        const lineEnd = /\r\n|\n|\r(?=[\s\S])/g;
        let start = 0;
        while ((match = lineEnd.exec(this.buffer)) !== null) {
            const event = this.processLine(this.buffer.slice(start, match.index));
            if (event) events.push(event);
            start = match.index + match[0].length;
        }
        this.buffer = this.buffer.slice(start);

        return events;
    }

    private processLine(line: string): ServerSentEvent | null {
        if (line === "") {
            const event = this.data.length > 0
                ? {event: this.event || "message", data: this.data.join("\n")}
                : null;
            this.event = "";
            this.data = [];
            return event;
        }

        if (line.startsWith(":")) return null;

        const colon = line.indexOf(":");
        const field = colon === -1 ? line : line.slice(0, colon);
        let value = colon === -1 ? "" : line.slice(colon + 1);
        if (value.startsWith(" ")) value = value.slice(1);

        if (field === "event") this.event = value;
        else if (field === "data") this.data.push(value);

        return null;
    }
}
