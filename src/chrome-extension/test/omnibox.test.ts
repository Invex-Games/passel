import {beforeEach, describe, expect, it, vi} from "vitest";
import type {SendDeps} from "../src/commands.js";
import {EMPTY_SUGGESTION, registerOmnibox, sendInput, suggestionFor} from "../src/omnibox.js";
import {installChrome, type FakeChrome} from "./chrome.js";

let fake: FakeChrome;
let deps: { send: ReturnType<typeof vi.fn>; fail: ReturnType<typeof vi.fn> };

beforeEach(() => {
    fake = installChrome();
    deps = {send: vi.fn(async () => undefined), fail: vi.fn(async () => undefined)};
});

describe("the suggestion", () => {
    it("says how to use the keyword while nothing is typed", () => {
        expect(suggestionFor("")).toBe(EMPTY_SUGGESTION);
        expect(suggestionFor("   ")).toBe(EMPTY_SUGGESTION);
    });

    it("offers a web address as a link and anything else as a note", () => {
        expect(suggestionFor(" https://passel.link/docs ")).toBe(
            "Send link: <url>https://passel.link/docs</url> <dim>to your devices</dim>");
        expect(suggestionFor("passel.link")).toBe("Send note: <match>passel.link</match> <dim>to your devices</dim>");
        expect(suggestionFor("ftp://files.test/a")).toMatch(/^Send note: /);
    });

    it("escapes the typed text, which Chrome reads as XML", () => {
        expect(suggestionFor(`<b>"Tom's" & co</b>`)).toBe(
            "Send note: <match>&lt;b&gt;&quot;Tom&apos;s&quot; &amp; co&lt;/b&gt;</match> <dim>to your devices</dim>");
        expect(suggestionFor("https://a.test/?q=1&r=2")).toContain("<url>https://a.test/?q=1&amp;r=2</url>");
    });

    it("repeats only the start of long text, without cutting an emoji in half", () => {
        const description = suggestionFor(`${"a".repeat(78)}🙂🙂🙂`);
        expect(description).toBe(`Send note: <match>${"a".repeat(78)}🙂…</match> <dim>to your devices</dim>`);

        const exact = "b".repeat(80);
        expect(suggestionFor(exact)).toContain(`<match>${exact}</match>`);
    });
});

describe("sending", () => {
    it("sends a web address as a link and anything else as a note, trimmed", async () => {
        await sendInput("  https://passel.link ", deps as unknown as SendDeps);
        await sendInput(" call the bank ", deps as unknown as SendDeps);

        expect(deps.send.mock.calls).toEqual([
            [{$type: "Link", name: null, url: "https://passel.link"}],
            [{$type: "Text", title: null, body: "call the bank"}],
        ]);
    });

    it("sends nothing for an empty line, and says so", async () => {
        await sendInput("  ", deps as unknown as SendDeps);

        expect(deps.send).not.toHaveBeenCalled();
        expect(deps.fail).toHaveBeenCalled();
    });
});

describe("the omnibox events", () => {
    beforeEach(() => {
        registerOmnibox(deps as unknown as SendDeps);
    });

    it("sets the default suggestion when the keyword is entered and as the text changes", () => {
        const suggest = vi.fn();
        fake.omnibox.onInputStarted.fire();
        fake.omnibox.onInputChanged.fire("https://passel.link", suggest);

        expect(fake.omnibox.setDefaultSuggestion.mock.calls).toEqual([
            [{description: EMPTY_SUGGESTION}],
            [{description: "Send link: <url>https://passel.link</url> <dim>to your devices</dim>"}],
        ]);
        // There are no other suggestions to offer.
        expect(suggest).toHaveBeenCalledWith([]);
    });

    it("sends on Enter, whichever way the user asked to open it", async () => {
        fake.omnibox.onInputEntered.fire("milk", "newForegroundTab");
        await Promise.resolve();

        expect(deps.send).toHaveBeenCalledWith({$type: "Text", title: null, body: "milk"});
    });
});
