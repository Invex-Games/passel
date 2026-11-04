// @vitest-environment jsdom
import {beforeEach, describe, expect, it, vi} from "vitest";
import type {ExtensionMessage} from "@passel/core";
import {installChrome, type FakeChrome} from "./chrome.js";
import {loadPopupPage, toastText} from "./popup-page.js";

type Dom = Awaited<ReturnType<typeof loadPopupPage>>["dom"];

let fake: FakeChrome;
let dom: Dom;

/** Opens the page as the popup or the side panel, with the service worker answering as below. */
async function open({sidePanel = false, signedIn = false} = {}): Promise<void> {
    fake = installChrome();
    fake.runtime.sendMessage.mockImplementation(async (message: unknown) => {
        switch ((message as ExtensionMessage).action) {
            case "getAuthState":
                return signedIn ? {signedIn: true, email: "ann@example.com"} : {signedIn: false};
            case "getPackets":
                return {
                    success: true,
                    page: {
                        packets: [{id: "p1", type: "text", title: "Grocery list", payload: "Milk", isImage: false}],
                        nextCursor: null,
                    },
                };
        }
        return undefined;
    });

    const page = await loadPopupPage({sidePanel});
    dom = page.dom;
    page.controller.startPopup();
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
