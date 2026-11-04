// @vitest-environment jsdom
import {beforeEach, describe, expect, it, vi} from "vitest";
import type {PacketData} from "@passel/core";
import {flushPromises, installChrome, type FakeChrome} from "./chrome.js";
import {loadPopupPage, press, toastText, type} from "./popup-page.js";

type Dom = Awaited<ReturnType<typeof loadPopupPage>>["dom"];

const PAGE = {url: "https://docs.flutter.dev/ui/widgets", title: "Widgets | Flutter"};

let fake: FakeChrome;
let dom: Dom;
let signedIn: boolean;
let send: ReturnType<typeof vi.fn<(data: PacketData, successMessage: string) => Promise<boolean>>>;

async function open({sidePanel = false, localData = {} as Record<string, unknown>} = {}): Promise<void> {
    fake = installChrome();
    fake.storage.local.data = {...localData};
    const page = await loadPopupPage({sidePanel});
    dom = page.dom;
    signedIn = true;
    send = vi.fn(async () => true);
    // Signed in, as the controller leaves the page: the field enabled and the buttons as the text allows.
    dom.freetextEl.disabled = false;
    page.composer.createComposer({isSignedIn: () => signedIn, send}).updateSendButtons();
    // The side panel reads whether the tabs permission was declined as it opens.
    await flushPromises();
}

describe("sending the page from the popup", () => {
    beforeEach(() => open());

    it("sends the active tab as a link, named by its title", async () => {
        fake.tabs.query.mockResolvedValue([PAGE]);

        dom.sendPageBtn.click();

        await vi.waitFor(() => expect(send).toHaveBeenCalledWith(
            {$type: "Link", name: "Widgets | Flutter", url: PAGE.url}, "Page sent!"));
        expect(fake.tabs.query).toHaveBeenCalledWith({active: true, currentWindow: true});
        // The popup opened from the tab, so activeTab covers it: no permission to ask for.
        expect(fake.permissions.request).not.toHaveBeenCalled();
        await vi.waitFor(() => expect(dom.sendPageBtn.disabled).toBe(false));
    });

    it("says so when the tab can't be read", async () => {
        fake.tabs.query.mockResolvedValue([{}]);

        dom.sendPageBtn.click();

        await vi.waitFor(() => expect(toastText()).toBe("Could not get current tab."));
        expect(send).not.toHaveBeenCalled();
    });
});

describe("sending the page from the side panel", () => {
    beforeEach(() => open({sidePanel: true}));

    it("asks for the tabs permission straight from the click, then sends the tab", async () => {
        fake.permissions.request.mockResolvedValue(true);
        fake.tabs.query.mockResolvedValue([PAGE]);

        dom.sendPageBtn.click();
        // Before anything is awaited: Chrome only shows the request in response to the click itself.
        expect(fake.permissions.request).toHaveBeenCalledWith({permissions: ["tabs"]});

        await vi.waitFor(() => expect(send).toHaveBeenCalledWith(
            {$type: "Link", name: "Widgets | Flutter", url: PAGE.url}, "Page sent!"));
        expect(fake.permissions.request.mock.invocationCallOrder[0])
            .toBeLessThan(fake.tabs.query.mock.invocationCallOrder[0]);
    });

    it("explains what to do when the permission is declined and the tab can't be read", async () => {
        fake.permissions.request.mockResolvedValue(false);
        fake.tabs.query.mockResolvedValue([{title: "Widgets | Flutter"}]);

        dom.sendPageBtn.click();

        await vi.waitFor(() => expect(toastText()).toBe(
            "To send pages from the side panel, allow Passel to see your tabs, or send this one from the toolbar button."));
        expect(send).not.toHaveBeenCalled();
    });

    it("still sends a tab that Passel was opened from, without the permission", async () => {
        fake.permissions.request.mockRejectedValue(new Error("This function must be called during a user gesture"));
        fake.tabs.query.mockResolvedValue([PAGE]);

        dom.sendPageBtn.click();

        await vi.waitFor(() => expect(send).toHaveBeenCalled());
        // Chrome couldn't ask, so the user didn't decline: the next send asks.
        expect(fake.storage.local.data).toEqual({});
    });

    /** Clicks Send Current Page and waits for the click to be done with. */
    async function sendPage(): Promise<void> {
        dom.sendPageBtn.click();
        await vi.waitFor(() => expect(dom.sendPageBtn.classList.contains("loading")).toBe(false));
    }

    it("asks once: declined, the next sends don't ask again, and still send a tab Passel was opened from", async () => {
        fake.permissions.request.mockResolvedValue(false);
        fake.tabs.query.mockResolvedValue([{title: "Widgets | Flutter"}]);

        await sendPage();
        expect(fake.storage.local.data).toEqual({tabsPermissionDeclined: true});

        await sendPage();
        expect(toastText()).toBe(
            "To send pages from the side panel, allow Passel to see your tabs, or send this one from the toolbar button.");

        fake.tabs.query.mockResolvedValue([PAGE]);
        await sendPage();
        expect(send).toHaveBeenCalledTimes(1);

        expect(fake.permissions.request).toHaveBeenCalledTimes(1);
        expect(fake.tabs.query).toHaveBeenCalledTimes(3);
    });

    it("doesn't ask in a side panel opened after an earlier decline", async () => {
        await open({sidePanel: true, localData: {tabsPermissionDeclined: true}});
        fake.tabs.query.mockResolvedValue([PAGE]);

        await sendPage();

        expect(fake.permissions.request).not.toHaveBeenCalled();
        expect(send).toHaveBeenCalled();
    });

    it("offers Allow after a decline, which asks again straight from its click and sends the page once granted", async () => {
        await open({sidePanel: true, localData: {tabsPermissionDeclined: true}});
        fake.tabs.query.mockResolvedValue([{title: "Widgets | Flutter"}]);
        await sendPage();
        expect(dom.toastAction.textContent).toBe("Allow");
        expect(dom.toastAction.classList.contains("hidden")).toBe(false);

        fake.permissions.request.mockResolvedValue(true);
        fake.tabs.query.mockResolvedValue([PAGE]);
        dom.toastAction.click();
        expect(fake.permissions.request).toHaveBeenCalledWith({permissions: ["tabs"]});

        await vi.waitFor(() => expect(send).toHaveBeenCalledWith(
            {$type: "Link", name: "Widgets | Flutter", url: PAGE.url}, "Page sent!"));
        expect(fake.storage.local.data).toEqual({});
    });

    it("keeps not asking when Allow is declined too", async () => {
        await open({sidePanel: true, localData: {tabsPermissionDeclined: true}});
        fake.tabs.query.mockResolvedValue([{title: "Widgets | Flutter"}]);
        await sendPage();

        dom.toastAction.click();
        await flushPromises();
        expect(send).not.toHaveBeenCalled();

        await sendPage();
        expect(fake.permissions.request).toHaveBeenCalledTimes(1);
        expect(fake.storage.local.data).toEqual({tabsPermissionDeclined: true});
    });
});

describe("sending typed text", () => {
    beforeEach(() => open());

    it("sends a web address as a link and clears the field", async () => {
        type(dom.freetextEl, "  https://passel.link  ");
        dom.sendTextBtn.click();

        await vi.waitFor(() => expect(send).toHaveBeenCalledWith(
            {$type: "Link", name: null, url: "https://passel.link"}, "Link sent!"));
        await vi.waitFor(() => expect(dom.freetextEl.value).toBe(""));
    });

    it("sends anything else as a note, and keeps the text when the send fails", async () => {
        send.mockResolvedValue(false);
        type(dom.freetextEl, "Milk\nEggs");
        dom.sendTextBtn.click();

        await vi.waitFor(() => expect(send).toHaveBeenCalledWith({$type: "Text", title: null, body: "Milk\nEggs"}, "Note sent!"));
        await vi.waitFor(() => expect(dom.sendTextBtn.classList.contains("loading")).toBe(false));
        expect(dom.freetextEl.value).toBe("Milk\nEggs");
    });

    it("sends on Enter, and Shift+Enter or an IME composition adds to the text instead", async () => {
        type(dom.freetextEl, "Milk");

        expect(press(dom.freetextEl, "Enter", {shiftKey: true}).defaultPrevented).toBe(false);
        expect(press(dom.freetextEl, "Enter", {isComposing: true}).defaultPrevented).toBe(false);
        expect(send).not.toHaveBeenCalled();

        expect(press(dom.freetextEl, "Enter").defaultPrevented).toBe(true);
        await vi.waitFor(() => expect(send).toHaveBeenCalledTimes(1));
    });

    it("keeps the send buttons disabled for blank text or while signed out", async () => {
        type(dom.freetextEl, "   ");
        expect(dom.sendTextBtn.disabled).toBe(true);

        type(dom.freetextEl, "Milk");
        expect(dom.sendTextBtn.disabled).toBe(false);

        signedIn = false;
        type(dom.freetextEl, "Milk!");
        expect(dom.sendTextBtn.disabled).toBe(true);
    });
});

describe("pasting", () => {
    beforeEach(() => open());

    function paste(text: string): Event {
        const event = new Event("paste", {bubbles: true, cancelable: true});
        Object.assign(event, {clipboardData: {getData: (format: string) => format === "text/plain" ? text : ""}});
        document.body.dispatchEvent(event);
        return event;
    }

    it("puts text pasted anywhere in the popup into the field", () => {
        const event = paste("  https://passel.link ");

        expect(event.defaultPrevented).toBe(true);
        expect(dom.freetextEl.value).toBe("https://passel.link");
        expect(document.activeElement).toBe(dom.freetextEl);
        expect(dom.sendTextBtn.disabled).toBe(false);
    });

    it("leaves a paste alone while signed out", () => {
        signedIn = false;
        expect(paste("hello").defaultPrevented).toBe(false);
        expect(dom.freetextEl.value).toBe("");
    });
});
