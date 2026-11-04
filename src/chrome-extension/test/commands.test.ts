import {beforeEach, describe, expect, it, vi} from "vitest";
import {MAX_TITLE_LENGTH} from "@passel/core";
import {handleCommand, OPEN_SIDE_PANEL_COMMAND, packetFromTab, SEND_PAGE_COMMAND, type SendDeps} from "../src/commands.js";
import {asChrome, installChrome, type FakeChrome} from "./chrome.js";

let fake: FakeChrome;
let deps: { send: ReturnType<typeof vi.fn>; fail: ReturnType<typeof vi.fn> };

const PAGE = asChrome<chrome.tabs.Tab>({id: 3, windowId: 1, url: "https://a.test/page", title: "A page"});

beforeEach(() => {
    fake = installChrome();
    deps = {send: vi.fn(async () => undefined), fail: vi.fn(async () => undefined)};
    vi.spyOn(console, "warn").mockImplementation(() => {
    });
});

function run(command: string, tab: chrome.tabs.Tab | undefined): Promise<void> {
    return handleCommand(command, tab, deps as unknown as SendDeps);
}

function selection(text: string): void {
    fake.scripting.executeScript.mockResolvedValue([{result: text}]);
}

describe("the send command", () => {
    it("sends the page as a link, named by its title, when nothing is selected", async () => {
        selection("");
        await run(SEND_PAGE_COMMAND, PAGE);

        expect(deps.send).toHaveBeenCalledWith({$type: "Link", name: "A page", url: "https://a.test/page"});
        expect(fake.scripting.executeScript).toHaveBeenCalledWith(expect.objectContaining({target: {tabId: 3}}));
    });

    it("sends the selected text as a note instead, trimmed", async () => {
        selection("  the parking permit number  ");
        await run(SEND_PAGE_COMMAND, PAGE);

        expect(deps.send).toHaveBeenCalledWith({$type: "Text", title: null, body: "the parking permit number"});
    });

    it("reads the selection with a function that returns it", async () => {
        selection("");
        await run(SEND_PAGE_COMMAND, PAGE);

        const {func} = fake.scripting.executeScript.mock.calls[0][0] as { func: () => string };
        vi.stubGlobal("getSelection", () => ({toString: () => "picked"}));
        expect(func()).toBe("picked");
        vi.stubGlobal("getSelection", () => null);
        expect(func()).toBe("");
    });

    it("sends the page when the tab can't be scripted, as on chrome:// pages", async () => {
        fake.scripting.executeScript.mockRejectedValue(new Error("Cannot access a chrome:// URL"));
        await run(SEND_PAGE_COMMAND, asChrome<chrome.tabs.Tab>({...PAGE, url: "chrome://settings", title: "Settings"}));

        expect(deps.send).toHaveBeenCalledWith({$type: "Link", name: "Settings", url: "chrome://settings"});
    });

    it("cuts a long title to what the API takes, and sends an untitled page without a name", async () => {
        selection("");
        await run(SEND_PAGE_COMMAND, asChrome<chrome.tabs.Tab>({...PAGE, title: "x".repeat(MAX_TITLE_LENGTH + 10)}));
        await run(SEND_PAGE_COMMAND, asChrome<chrome.tabs.Tab>({...PAGE, title: ""}));

        expect(deps.send.mock.calls[0][0].name).toHaveLength(MAX_TITLE_LENGTH);
        expect(deps.send.mock.calls[1][0].name).toBeNull();
    });

    it.each<[string, chrome.tabs.Tab | undefined]>([
        ["no tab (focus outside a page)", undefined],
        ["a tab whose address it may not read", asChrome<chrome.tabs.Tab>({id: 3, windowId: 1})],
    ])("fails, sending nothing, for %s", async (_, tab) => {
        selection("");
        await run(SEND_PAGE_COMMAND, tab);

        expect(deps.send).not.toHaveBeenCalled();
        expect(deps.fail).toHaveBeenCalled();
    });

    it("sends the page of a tab without an id, which can't be asked for its selection", async () => {
        const tab = asChrome<chrome.tabs.Tab>({windowId: 1, url: "https://a.test/", title: "A"});
        expect(await packetFromTab(tab)).toEqual({$type: "Link", name: "A", url: "https://a.test/"});
        expect(fake.scripting.executeScript).not.toHaveBeenCalled();
    });
});

describe("the side panel command", () => {
    it("opens the side panel in the window, before anything is awaited", () => {
        void run(OPEN_SIDE_PANEL_COMMAND, PAGE);
        // Synchronously: Chrome only opens the panel in response to the user action itself.
        expect(fake.sidePanel.open).toHaveBeenCalledWith({windowId: 1});
    });

    it("does nothing without a window, and survives Chrome refusing", async () => {
        await run(OPEN_SIDE_PANEL_COMMAND, undefined);
        expect(fake.sidePanel.open).not.toHaveBeenCalled();

        fake.sidePanel.open.mockRejectedValue(new Error("sidePanel.open() may only be called in response to a user gesture."));
        await expect(run(OPEN_SIDE_PANEL_COMMAND, PAGE)).resolves.toBeUndefined();
    });
});

it("ignores commands it doesn't know", async () => {
    await run("something-else", PAGE);
    expect(deps.send).not.toHaveBeenCalled();
    expect(deps.fail).not.toHaveBeenCalled();
    expect(fake.sidePanel.open).not.toHaveBeenCalled();
});
