// @vitest-environment jsdom
import {beforeEach, describe, expect, it, vi} from "vitest";
import type {ExtensionMessage, Packet} from "@passel/core";
import {EXTENSION_ID, installChrome, type FakeChrome, type FakePort} from "./chrome.js";
import {loadPopupPage, toastText} from "./popup-page.js";

type Dom = Awaited<ReturnType<typeof loadPopupPage>>["dom"];

const ANNS_PACKET: Packet = {id: "p1", type: "text", title: "Grocery list", payload: "Milk", isImage: false};
const BOBS_PACKET: Packet = {id: "p2", type: "text", title: "Hardware list", payload: "Nails", isImage: false};

let fake: FakeChrome;
let dom: Dom;

/** Who the service worker says is signed in, and what it serves them. A test can change both as it goes. */
let account: string | null;
let packets: Packet[];

/** Opens the page as the popup or the side panel, with the service worker answering as below. */
async function open({sidePanel = false, signedIn = false} = {}): Promise<void> {
    account = signedIn ? "ann@example.com" : null;
    packets = [ANNS_PACKET];

    fake = installChrome();
    fake.runtime.sendMessage.mockImplementation(async (message: unknown) => {
        switch ((message as ExtensionMessage).action) {
            case "getAuthState":
                return account ? {signedIn: true, email: account} : {signedIn: false};
            case "getPackets":
                return {success: true, page: {packets, nextCursor: null}};
            case "signOut":
                return {success: true};
        }
        return undefined;
    });

    const page = await loadPopupPage({sidePanel});
    dom = page.dom;
    page.controller.startPopup();
}

/** The service worker's word to every open page that someone signed in or out, as `account` now. */
function sessionChanges(): unknown[] {
    return fake.runtime.onMessage.fire({type: "sessionChanged"}, {id: EXTENSION_ID}, () => {
    });
}

function tileTitles(): string[] {
    return [...dom.feedList.querySelectorAll(".packet-title")].map(title => title.textContent ?? "");
}

function feedPort(): FakePort {
    return fake.runtime.connect.mock.results[0].value as FakePort;
}

function showsSignedIn(): boolean {
    return !dom.authSignedIn.classList.contains("hidden") && !dom.composerEl.classList.contains("hidden");
}

describe("the popup", () => {
    beforeEach(() => open());

    it("moves into the side panel of its window and closes", async () => {
        const close = vi.spyOn(window, "close").mockImplementation(() => {
        });
        await vi.waitFor(() => expect(fake.windows.getCurrent).toHaveBeenCalled());
        await Promise.resolve();

        dom.sidePanelBtn.click();

        // Opened from the click itself: Chrome only opens the side panel in response to a user action.
        expect(fake.sidePanel.open).toHaveBeenCalledWith({windowId: 7});
        await vi.waitFor(() => expect(close).toHaveBeenCalled());
    });

    it("stays open and says why when Chrome won't open the side panel", async () => {
        const close = vi.spyOn(window, "close").mockImplementation(() => {
        });
        fake.sidePanel.open.mockRejectedValue(new Error("No active side panel for windowId: 7"));
        await vi.waitFor(() => expect(fake.windows.getCurrent).toHaveBeenCalled());
        await Promise.resolve();

        dom.sidePanelBtn.click();

        await vi.waitFor(() => expect(toastText()).toBe("Couldn't open the side panel: No active side panel for windowId: 7"));
        expect(close).not.toHaveBeenCalled();
    });
});

describe("the side panel", () => {
    it("doesn't offer to open itself", async () => {
        await open({sidePanel: true});
        expect(dom.inSidePanel).toBe(true);
        expect(dom.sidePanelBtn.classList.contains("popup-only")).toBe(true);

        dom.sidePanelBtn.click();

        expect(fake.windows.getCurrent).not.toHaveBeenCalled();
        expect(fake.sidePanel.open).not.toHaveBeenCalled();
    });

    it("runs the popup's feed: signed in, it holds a live port and shows the packets", async () => {
        await open({sidePanel: true, signedIn: true});

        await vi.waitFor(() => expect(dom.userEmailEl.textContent).toBe("ann@example.com"));
        await vi.waitFor(() => expect(dom.feedList.querySelector(".packet-title")?.textContent).toBe("Grocery list"));

        expect(fake.runtime.connect).toHaveBeenCalledWith({name: "feed"});
        expect(dom.composerEl.classList.contains("hidden")).toBe(false);
    });
});

describe("a sign-in or sign-out elsewhere", () => {
    it("moves a signed-in side panel to the new account, with only that account's feed shown and cached", async () => {
        await open({sidePanel: true, signedIn: true});
        await vi.waitFor(() => expect(tileTitles()).toEqual(["Grocery list"]));

        // Signed out and in as bob from the popup beside it.
        account = "bob@example.com";
        packets = [BOBS_PACKET];
        sessionChanges();

        await vi.waitFor(() => expect(dom.userEmailEl.textContent).toBe("bob@example.com"));
        await vi.waitFor(() => expect(tileTitles()).toEqual(["Hardware list"]));
        await vi.waitFor(() => expect((fake.storage.local.data.packetsCache as { packets: Packet[] }).packets)
            .toEqual([BOBS_PACKET]));
        expect(feedPort().disconnect).not.toHaveBeenCalled();
    });

    it("signs a side panel out when the popup signs out, forgetting the feed", async () => {
        await open({sidePanel: true, signedIn: true});
        await vi.waitFor(() => expect(tileTitles()).toEqual(["Grocery list"]));

        account = null;
        sessionChanges();

        await vi.waitFor(() => expect(showsSignedIn()).toBe(false));
        expect(dom.authSignedOut.classList.contains("hidden")).toBe(false);
        expect(tileTitles()).toEqual([]);
        expect(feedPort().disconnect).toHaveBeenCalled();
    });

    it("signs in a signed-out side panel when a shortcut or the popup signs in", async () => {
        await open({sidePanel: true});
        await vi.waitFor(() => expect(dom.authSignedOut.classList.contains("hidden")).toBe(false));

        account = "ann@example.com";
        sessionChanges();

        await vi.waitFor(() => expect(showsSignedIn()).toBe(true));
        expect(dom.userEmailEl.textContent).toBe("ann@example.com");
        expect(fake.runtime.connect).toHaveBeenCalledWith({name: "feed"});
        await vi.waitFor(() => expect(tileTitles()).toEqual(["Grocery list"]));
    });

    it("keeps the feed of the page that signed in, whichever word of it comes first", async () => {
        await open();
        await vi.waitFor(() => expect(dom.authSignedOut.classList.contains("hidden")).toBe(false));

        // The service worker announces the sign-in before it answers the page that asked for it.
        const answer = fake.runtime.sendMessage.getMockImplementation()!;
        fake.runtime.sendMessage.mockImplementation(async (message: unknown) => {
            if ((message as ExtensionMessage).action !== "signIn") return answer(message);
            account = "ann@example.com";
            sessionChanges();
            await new Promise(resolve => setTimeout(resolve, 20));
            return {success: true, email: account};
        });

        dom.signInBtn.click();

        await vi.waitFor(() => expect(toastText()).toBe("Signed in!"));
        await vi.waitFor(() => expect(tileTitles()).toEqual(["Grocery list"]));
        expect(fake.runtime.connect).toHaveBeenCalledTimes(1);
    });

    it("leaves alone the messages other pages send the service worker, and anything from another extension", async () => {
        await open({sidePanel: true, signedIn: true});
        await vi.waitFor(() => expect(showsSignedIn()).toBe(true));
        account = "bob@example.com";
        fake.runtime.sendMessage.mockClear();

        const answers = fake.runtime.onMessage.fire({action: "getPackets"}, {id: EXTENSION_ID}, () => {
        });
        fake.runtime.onMessage.fire({type: "sessionChanged"}, {id: "another-extension"}, () => {
        });

        // Returning nothing leaves the answer to the service worker.
        expect(answers).toEqual([undefined]);
        expect(sessionChanges()).toEqual([undefined]);
        await vi.waitFor(() => expect(dom.userEmailEl.textContent).toBe("bob@example.com"));
        expect(fake.runtime.sendMessage.mock.calls.filter(([m]) => (m as ExtensionMessage).action === "getAuthState"))
            .toHaveLength(1);
    });
});
