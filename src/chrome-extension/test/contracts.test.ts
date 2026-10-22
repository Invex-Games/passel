import {readdirSync, readFileSync} from "node:fs";
import {dirname, join} from "node:path";
import {fileURLToPath} from "node:url";
import {describe, expect, it} from "vitest";
import {FeedStore, type FeedCache} from "../src/feed.js";
import {parsePacket, parsePackets, toLiveEvent} from "../src/packets.js";
import type {CachedFeed} from "../src/cache.js";
import type {Packet} from "../src/types.js";

// The wire contract, as the API's integration tests write it. contracts/ at the repository root holds sample
// payloads the API serialized (fixtures/) and the feed sequences every client must agree on
// (feed-scenarios.json). Both go through the real parsers and the real feed store here, so a change to the
// wire shape or to the feed rules on either side fails this suite. See contracts/README.md.
const contractsDir = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "contracts");
const fixturesDir = join(contractsDir, "fixtures");

/** A packet as the API writes it. */
type WirePacket = Record<string, unknown> & { $type: string; id: string };

function readJson(path: string): unknown {
    return JSON.parse(readFileSync(path, "utf8"));
}

/** What the parser must make of a wire packet: the one place this suite spells the field mapping out. */
function expectedPacket(raw: WirePacket): Packet {
    switch (raw.$type) {
        case "Link":
            return {id: raw.id, type: "link", title: raw.name as string | null, payload: raw.url as string, isImage: false};
        case "Text":
            return {id: raw.id, type: "text", title: raw.title as string | null, payload: raw.body as string, isImage: false};
        case "File":
            return {
                id: raw.id,
                type: "file",
                title: raw.fileName as string | null,
                payload: raw.parcelId as string,
                isImage: raw.isImage === true,
            };
        default:
            throw new Error(`This suite has no mapping for a ${raw.$type} packet`);
    }
}

// ── Fixtures ─────────────────────────────────────────────

const fixtureChecks: Record<string, (json: unknown) => void> = {
    "list-page.json": json => {
        const page = json as { packets: WirePacket[]; nextCursor: unknown };
        const packets = parsePackets(page.packets);

        // Every packet parses, in order, to what its fields say; and the page shows every kind there is
        expect(packets).toEqual(page.packets.map(expectedPacket));
        expect(packets.map(p => p.type).sort()).toEqual(["file", "link", "text"]);
        expect(typeof page.nextCursor).toBe("string");
    },
    "create-response.json": json => {
        expect(parsePacket(json)).toEqual(expectedPacket(json as WirePacket));
    },
    "packet-created-event.json": json => {
        expect(toLiveEvent("packet-created", JSON.stringify(json)))
            .toEqual({kind: "created", packet: expectedPacket(json as WirePacket)});
    },
    "packet-deleted-event.json": json => {
        const {id} = json as { id: unknown };
        expect(typeof id).toBe("string");
        expect(toLiveEvent("packet-deleted", JSON.stringify(json))).toEqual({kind: "deleted", id});
    },
    "packet-updated-event.json": json => {
        // A link with its preview, in the shape of a created packet. The feed ignores the event until it shows
        // previews (roadmap 2.C1); the packet in it still parses, and the preview field is simply not read.
        expect(toLiveEvent("packet-updated", JSON.stringify(json))).toBeNull();
        expect(parsePacket(json)).toEqual(expectedPacket(json as WirePacket));
    },
};

describe("contract fixtures", () => {
    it("every fixture the API writes has a check here", () => {
        expect(readdirSync(fixturesDir).sort()).toEqual(Object.keys(fixtureChecks).sort());
    });

    it.each(Object.keys(fixtureChecks))("%s parses with the real parser", name => {
        fixtureChecks[name](readJson(join(fixturesDir, name)));
    });
});

// ── Feed scenarios ───────────────────────────────────────

interface Serves {
    cursor: string | null;
    packets: string[];
    nextCursor: string | null;
}

interface Step {
    action: string;
    serves?: Serves;
    packet?: string;
    id?: string;
    expect: { packets: string[]; nextCursor?: string | null };
}

interface ScenarioFile {
    packets: Record<string, WirePacket>;
    scenarios: { name: string; steps: Step[] }[];
}

const scenarioFile = readJson(join(contractsDir, "feed-scenarios.json")) as ScenarioFile;

function wire(id: string): WirePacket {
    const packet = scenarioFile.packets[id];
    if (!packet) throw new Error(`feed-scenarios.json defines no packet ${id}`);
    return packet;
}

function named(step: Step, field: "packet" | "id"): string {
    const value = step[field];
    if (!value) throw new Error(`A ${step.action} step needs a ${field}`);
    return value;
}

class MemoryCache implements FeedCache {
    feed: CachedFeed | null = null;

    async load() {
        return this.feed;
    }

    async save(feed: CachedFeed) {
        this.feed = feed;
    }

    async clear() {
        this.feed = null;
    }
}

/** Drives the feed the way the popup does for each action; a live event goes through the event parser. */
async function run(feed: FeedStore, step: Step): Promise<void> {
    switch (step.action) {
        case "open":
            await feed.showCached();
            await feed.refresh();
            return;
        case "refresh":
            await feed.refresh();
            return;
        case "loadMore":
            await feed.loadMore();
            return;
        case "resync":
            await feed.syncLatest();
            return;
        case "created": {
            const event = toLiveEvent("packet-created", JSON.stringify(wire(named(step, "packet"))));
            if (event?.kind !== "created") throw new Error(`The packet-created event for ${step.packet} did not parse`);
            feed.applyCreated(event.packet);
            return;
        }
        case "deleted": {
            const event = toLiveEvent("packet-deleted", JSON.stringify({id: named(step, "id")}));
            if (event?.kind !== "deleted") throw new Error(`The packet-deleted event for ${step.id} did not parse`);
            feed.applyDeleted(event.id);
            return;
        }
        case "updated": {
            // Ignored by this version of the feed, so the list is left alone. Once the feed store handles the
            // event, apply it here; the scenarios then check that it changes no ids.
            const event = toLiveEvent("packet-updated", JSON.stringify(wire(named(step, "packet"))));
            if (event !== null) throw new Error("The feed store now handles packet-updated; teach this interpreter to apply it");
            return;
        }
        case "hide":
            feed.hide(named(step, "id"));
            return;
        case "unhide":
            feed.unhide(named(step, "id"));
            return;
        default:
            throw new Error(`feed-scenarios.json uses an action this suite does not know: ${step.action}`);
    }
}

describe("feed scenarios", () => {
    for (const scenario of scenarioFile.scenarios) {
        it(scenario.name, async () => {
            let serves: Serves | undefined;
            let fetches = 0;

            const feed = new FeedStore({
                fetchPage: async cursor => {
                    const page = serves;
                    serves = undefined;
                    if (!page) throw new Error(`The feed fetched (cursor ${cursor ?? "none"}) in a step that serves nothing`);
                    expect(cursor ?? null, "the cursor asked for").toBe(page.cursor);
                    fetches++;
                    return {packets: parsePackets(page.packets.map(wire)), nextCursor: page.nextCursor};
                },
            }, new MemoryCache());

            for (const step of scenario.steps) {
                serves = step.serves;
                const fetchesBefore = fetches;

                await run(feed, step);

                expect(fetches - fetchesBefore, `fetches during ${step.action}`).toBe(step.serves ? 1 : 0);
                expect(feed.state.packets.map(p => p.id), `the list after ${step.action}`).toEqual(step.expect.packets);
                if (step.expect.nextCursor !== undefined) {
                    expect(feed.state.nextCursor, `the cursor after ${step.action}`).toBe(step.expect.nextCursor);
                }
            }
        });
    }
});
