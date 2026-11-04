import {afterEach, beforeEach, describe, expect, it, vi} from "vitest";
import type {ExtensionMessage, PacketData} from "@passel/core";
import {EXTENSION_ID, fakePort, flushPromises, installChrome, type FakeChrome} from "./chrome.js";

const auth = vi.hoisted(() => ({
    getAccountName: vi.fn(),
    getLastDevUser: vi.fn(),
    getToken: vi.fn(),
    onSessionChanged: vi.fn(),
    signIn: vi.fn(),
    signOut: vi.fn(),
}));
const api = vi.hoisted(() => ({deletePacket: vi.fn(), getPackets: vi.fn(), sendPacket: vi.fn()}));
const live = vi.hoisted(() => ({addFeedPort: vi.fn(), restartLive: vi.fn()}));
vi.mock("../src/auth.js", () => auth);
vi.mock("../src/api.js", () => api);
vi.mock("../src/live.js", () => live);

const UNDO_MS = 4000;

let fake: FakeChrome;

beforeEach(async () => {
    fake = installChrome();
    vi.useFakeTimers();
    api.sendPacket.mockResolvedValue({success: true});
    api.deletePacket.mockResolvedValue({success: true});
    auth.signOut.mockResolvedValue(undefined);
    auth.getLastDevUser.mockResolvedValue(null);

    // The service worker registers its listeners as it loads, on whichever chrome it finds.
    vi.resetModules();
    await import("../src/background.js");
});

afterEach(() => {
    vi.useRealTimers();
});

/** Sends a message as the popup would, returning whether the worker kept the channel open and its answer. */
function message<R>(msg: ExtensionMessage, sender: chrome.runtime.MessageSender = {id: EXTENSION_ID}) {
    let respond!: (response: unknown) => void;
    const response = new Promise<R>(resolve => respond = value => resolve(value as R));
    const [keptOpen] = fake.runtime.onMessage.fire(msg, sender, respond);
    return {keptOpen, response};
}

function badge(): string | undefined {
    return fake.action.setBadgeText.mock.lastCall?.[0].text;
}

describe("installing", () => {
    it("creates the three send menus and drops the token earlier versions kept in local storage", async () => {
        fake.storage.local.data.idToken = "old";

        await fake.runtime.onInstalled.fire({reason: "update"})[0];

        expect(fake.contextMenus.removeAll).toHaveBeenCalled();
        expect(fake.contextMenus.create.mock.calls.map(([menu]) => [menu.id, menu.contexts, menu.title])).toEqual([
            ["passel-link", ["link"], "Send link to Passel"],
            ["passel-selection", ["selection"], "Send selection to Passel"],
            ["passel-image", ["image"], "Send image URL to Passel"],
        ]);
        expect(fake.storage.local.data.idToken).toBeUndefined();
    });
});

describe("messages from the popup", () => {
    it("ignores messages from anyone but this extension", () => {
        const {keptOpen} = message({action: "sendPacket", data: {$type: "Text", title: null, body: "x"}}, {id: "other"});

        expect(keptOpen).toBeUndefined();
        expect(api.sendPacket).not.toHaveBeenCalled();
    });

    it("reports the session without ever showing a sign-in window", async () => {
        auth.getToken.mockResolvedValue("token");
        auth.getAccountName.mockReturnValue("ann@example.com");

        const {keptOpen, response} = message({action: "getAuthState"});

        expect(keptOpen).toBe(true);
        expect(await response).toEqual({signedIn: true, email: "ann@example.com"});
        expect(auth.getToken).toHaveBeenCalledWith(false);
    });

    it("offers the last development user when signed out", async () => {
        auth.getToken.mockResolvedValue(null);
        auth.getLastDevUser.mockResolvedValue("alice");

        expect(await message({action: "getAuthState"}).response).toEqual({signedIn: false, lastDevUser: "alice"});
    });

    it("signs in, as a development user when one is named, and passes on a failure", async () => {
        auth.signIn.mockResolvedValueOnce("dev-user:alice").mockRejectedValueOnce(new Error("The user did not approve access."));
        auth.getAccountName.mockReturnValue("alice (dev)");

        expect(await message({action: "signIn", devUser: "alice"}).response).toEqual({success: true, email: "alice (dev)"});
        expect(auth.signIn).toHaveBeenCalledWith("alice");
        expect(await message({action: "signIn"}).response).toEqual({success: false, error: "The user did not approve access."});
    });

    it("signs out", async () => {
        expect(await message({action: "signOut"}).response).toEqual({success: true});
        expect(auth.signOut).toHaveBeenCalled();
    });

    it("sends, allowing a sign-in window, and flashes the badge", async () => {
        const data: PacketData = {$type: "Text", title: null, body: "Milk"};

        expect(await message({action: "sendPacket", data}).response).toEqual({success: true});
        expect(api.sendPacket).toHaveBeenCalledWith(data, true);
        expect(badge()).toBe("✓");
        expect(fake.action.setBadgeBackgroundColor).toHaveBeenLastCalledWith({color: "#2E7D32"});

        await vi.advanceTimersByTimeAsync(4000);
        expect(badge()).toBe("");
    });

    it("loads a page by its cursor", async () => {
        api.getPackets.mockResolvedValue({success: true, page: {packets: [], nextCursor: null}});

        expect(await message({action: "getPackets", cursor: "c1"}).response).toMatchObject({success: true});
        expect(api.getPackets).toHaveBeenCalledWith("c1");
    });

    it("hands the popup's feed port to the live connection, and nothing else", () => {
        const feed = fakePort("feed");
        fake.runtime.onConnect.fire(feed);
        fake.runtime.onConnect.fire(fakePort("something-else"));
        fake.runtime.onConnect.fire(fakePort("feed", "another-extension"));

        expect(live.addFeedPort.mock.calls).toEqual([[feed]]);
    });
});

describe("session changes", () => {
    /** Signs someone in or out, as auth.ts reports it. */
    function sessionChanges(): void {
        const [[listener]] = auth.onSessionChanged.mock.calls;
        listener();
    }

    it("start the event stream again and tell every open page", () => {
        sessionChanges();

        expect(live.restartLive).toHaveBeenCalledTimes(1);
        expect(fake.runtime.sendMessage).toHaveBeenCalledWith({type: "sessionChanged"});
    });

    it("don't mind that no page is open to hear it", async () => {
        fake.runtime.sendMessage.mockRejectedValue(new Error("Could not establish connection. Receiving end does not exist."));

        expect(sessionChanges).not.toThrow();
        await flushPromises();
    });
});

describe("deleting with undo", () => {
    it("sends the deletion once the undo window has passed, and answers with its result", async () => {
        const {keptOpen, response} = message({action: "deletePacket", id: "p1", delayMs: UNDO_MS});
        expect(keptOpen).toBe(true);

        await vi.advanceTimersByTimeAsync(UNDO_MS - 1);
        expect(api.deletePacket).not.toHaveBeenCalled();

        await vi.advanceTimersByTimeAsync(1);
        expect(api.deletePacket).toHaveBeenCalledWith("p1", true);
        expect(await response).toEqual({success: true});
    });

    it("doesn't send a deletion that's undone in time", async () => {
        const deletion = message({action: "deletePacket", id: "p1", delayMs: UNDO_MS});
        await vi.advanceTimersByTimeAsync(UNDO_MS - 1);

        const cancel = message({action: "cancelDelete", id: "p1"});

        expect(cancel.keptOpen).toBe(false);
        expect(await cancel.response).toEqual({cancelled: true});
        expect(await deletion.response).toEqual({success: false, cancelled: true});
        await vi.advanceTimersByTimeAsync(UNDO_MS * 2);
        expect(api.deletePacket).not.toHaveBeenCalled();
    });

    it("says it's too late to undo a deletion that was already sent", async () => {
        message({action: "deletePacket", id: "p1", delayMs: UNDO_MS});
        await vi.advanceTimersByTimeAsync(UNDO_MS);

        expect(await message({action: "cancelDelete", id: "p1"}).response).toEqual({cancelled: false});
        expect(api.deletePacket).toHaveBeenCalledTimes(1);
    });

    it("can't undo a deletion it never scheduled", async () => {
        expect(await message({action: "cancelDelete", id: "nope"}).response).toEqual({cancelled: false});
    });

    it("replaces an earlier deletion of the same packet, sending only the last", async () => {
        const first = message({action: "deletePacket", id: "p1", delayMs: UNDO_MS});
        await vi.advanceTimersByTimeAsync(UNDO_MS - 1000);
        const second = message({action: "deletePacket", id: "p1", delayMs: UNDO_MS});

        expect(await first.response).toEqual({success: false, cancelled: true});
        await vi.advanceTimersByTimeAsync(UNDO_MS - 1);
        expect(api.deletePacket).not.toHaveBeenCalled();
        await vi.advanceTimersByTimeAsync(1);
        expect(await second.response).toEqual({success: true});
        expect(api.deletePacket).toHaveBeenCalledTimes(1);
    });

    it("keeps separate packets on separate timers", async () => {
        message({action: "deletePacket", id: "p1", delayMs: UNDO_MS});
        message({action: "deletePacket", id: "p2", delayMs: UNDO_MS});
        await message({action: "cancelDelete", id: "p1"}).response;

        await vi.advanceTimersByTimeAsync(UNDO_MS);
        expect(api.deletePacket.mock.calls).toEqual([["p2", true]]);
    });
});

describe("context menus", () => {
    async function click(info: Partial<chrome.contextMenus.OnClickData>): Promise<void> {
        await fake.contextMenus.onClicked.fire(info)[0];
    }

    it.each<[string, Partial<chrome.contextMenus.OnClickData>, PacketData]>([
        ["a link, named by its selected text", {menuItemId: "passel-link", linkUrl: "https://a.test/", selectionText: "A"},
            {$type: "Link", name: "A", url: "https://a.test/"}],
        ["a link without a name", {menuItemId: "passel-link", linkUrl: "https://a.test/"},
            {$type: "Link", name: null, url: "https://a.test/"}],
        ["the selection as a note", {menuItemId: "passel-selection", selectionText: "Some text"},
            {$type: "Text", title: null, body: "Some text"}],
        ["an image URL as a link named by its file", {menuItemId: "passel-image", srcUrl: "https://a.test/img/my%20cat.jpg?w=2"},
            {$type: "Link", name: "my cat.jpg", url: "https://a.test/img/my%20cat.jpg?w=2"}],
        ["an image URL without a file name", {menuItemId: "passel-image", srcUrl: "https://a.test/images/12345"},
            {$type: "Link", name: null, url: "https://a.test/images/12345"}],
    ])("sends %s, allowing a sign-in window", async (_, info, data) => {
        await click(info);
        expect(api.sendPacket).toHaveBeenCalledWith(data, true);
        expect(badge()).toBe("✓");
    });

    it.each<[string, Partial<chrome.contextMenus.OnClickData>]>([
        ["a data: image, which only exists in the page", {menuItemId: "passel-image", srcUrl: "data:image/png;base64,AAAA"}],
        ["a link without a URL", {menuItemId: "passel-link"}],
        ["an empty selection", {menuItemId: "passel-selection", selectionText: ""}],
        ["a menu it doesn't know", {menuItemId: "passel-other"}],
    ])("sends nothing for %s, and says so on the badge", async (_, info) => {
        await click(info);
        expect(api.sendPacket).not.toHaveBeenCalled();
        expect(badge()).toBe("!");
        expect(fake.action.setBadgeBackgroundColor).toHaveBeenLastCalledWith({color: "#BA1A1A"});
    });

    it("shows a failed send on the badge", async () => {
        api.sendPacket.mockResolvedValue({success: false, error: "Couldn't reach Passel."});
        await click({menuItemId: "passel-selection", selectionText: "Some text"});
        expect(badge()).toBe("!");
    });
});

describe("keyboard commands and the omnibox", () => {
    it("sends the page from the send command, allowing a sign-in window, with the outcome on the badge", async () => {
        fake.commands.onCommand.fire("send-current-page", {id: 3, windowId: 1, url: "https://a.test/", title: "A"});
        await flushPromises();

        expect(api.sendPacket).toHaveBeenCalledWith({$type: "Link", name: "A", url: "https://a.test/"}, true);
        expect(badge()).toBe("✓");
    });

    it("shows on the badge when the send command had nothing to send", async () => {
        fake.commands.onCommand.fire("send-current-page", undefined);
        await flushPromises();

        expect(api.sendPacket).not.toHaveBeenCalled();
        expect(badge()).toBe("!");
    });

    it("opens the side panel from its command", () => {
        fake.commands.onCommand.fire("open-side-panel", {id: 3, windowId: 9});
        expect(fake.sidePanel.open).toHaveBeenCalledWith({windowId: 9});
    });

    it("sends what was typed after the keyword, allowing a sign-in window", async () => {
        fake.omnibox.onInputEntered.fire("https://passel.link", "currentTab");
        await flushPromises();

        expect(api.sendPacket).toHaveBeenCalledWith({$type: "Link", name: null, url: "https://passel.link"}, true);
        expect(badge()).toBe("✓");
    });

    it("suggests what Enter will send as the user types", () => {
        const suggest = vi.fn();
        fake.omnibox.onInputStarted.fire();
        fake.omnibox.onInputChanged.fire("call the bank", suggest);

        expect(fake.omnibox.setDefaultSuggestion.mock.calls.map(([s]) => s.description)).toEqual([
            "Type a note or paste a link, then press Enter to send it to your devices",
            "Send note: <match>call the bank</match> <dim>to your devices</dim>",
        ]);
        expect(suggest).toHaveBeenCalledWith([]);
    });
});
