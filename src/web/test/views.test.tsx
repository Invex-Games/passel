import type {FeedState, Packet} from "@passel/core";
import {render} from "preact-render-to-string";
import {describe, expect, it} from "vitest";
import type {AppState} from "../src/app/controller.js";
import {ConfigErrorPage} from "../src/app/views/ConfigErrorPage.js";
import {FeedEndRow, FeedPage, type FeedPageActions} from "../src/app/views/FeedPage.js";
import {SignInPage} from "../src/app/views/SignInPage.js";
import {Tile} from "../src/app/views/Tile.js";

// The views rendered to HTML, as the browser would first draw them: what each state shows and what it leaves out.

const actions: FeedPageActions = {
    refresh: () => {
    },
    loadMore: () => {
    },
    signOut: () => {
    },
    dismissNotice: () => {
    },
};

const link = (patch: Partial<Packet> = {}): Packet =>
    ({id: "l", type: "link", title: "Example", payload: "https://www.example.com/page", isImage: false, ...patch});

const state = (feed: Partial<FeedState>, patch: Partial<AppState> = {}): AppState => ({
    auth: {status: "signedIn", account: "alice@example.com"},
    feed: {packets: [], nextCursor: null, lastSyncedAt: null, loaded: true, loadingMore: false, loadMoreError: null, ...feed},
    live: false,
    refreshing: false,
    loadError: null,
    notice: null,
    ...patch,
});

describe("Tile", () => {
    it("shows a link with its preview, opening in a new tab and loading the image without a referrer", () => {
        const html = render(<Tile packet={link({
            preview: {title: "Example Domain", description: "For examples.", imageUrl: "https://example.com/og.png"},
        })}/>);

        expect(html).toContain('href="https://www.example.com/page" target="_blank" rel="noopener noreferrer"');
        expect(html).toContain("Example Domain");
        expect(html).toContain("For examples.");
        expect(html).toContain("example.com</span>");
        expect(html).toMatch(/<img[^>]+src="https:\/\/example.com\/og.png"[^>]+referrerpolicy="no-referrer"/i);
    });

    it("shows a link without a preview as just the link", () => {
        const html = render(<Tile packet={link()}/>);

        expect(html).toContain("https://www.example.com/page");
        expect(html).not.toContain("preview");
    });

    it("shows a link that isn't http(s) as text, never as a link", () => {
        const html = render(<Tile packet={link({payload: "javascript:alert(1)"})}/>);

        expect(html).not.toContain("<a ");
        expect(html).toContain("javascript:alert(1)");
    });

    it("escapes what was sent", () => {
        const html = render(<Tile packet={{id: "t", type: "text", title: "<b>hi</b>", payload: "<script>x</script>", isImage: false}}/>);

        expect(html).not.toContain("<script>");
        expect(html).toContain("&lt;script>");
        expect(html).toContain("&lt;b>hi&lt;/b>");
    });

    it("collapses a long note behind Show more, and calls an item without a title by what it is", () => {
        const html = render(<Tile packet={{id: "t", type: "text", title: null, payload: "x".repeat(700), isImage: false}}/>);

        expect(html).toContain("tile-body clamped");
        expect(html).toContain("Show more");
        expect(html).toContain(">Note</h2>");
    });

    it("says files can't be opened here yet", () => {
        const html = render(<Tile packet={{id: "f", type: "file", title: "photo.png", payload: "p1", isImage: true}}/>);

        expect(html).toContain("photo.png");
        expect(html).toContain("Files can't be opened here yet.");
    });
});

describe("FeedPage", () => {
    it("lists the items newest first under the account, sync label and sign-out", () => {
        const html = render(<FeedPage now={0} actions={actions} state={state({
            packets: [link({id: "new", title: "Newest"}), link({id: "old", title: "Oldest"})],
        }, {live: true})}/>);

        expect(html.indexOf("Newest")).toBeLessThan(html.indexOf("Oldest"));
        expect(html).toContain("alice@example.com");
        expect(html).toContain("Live");
        expect(html).toContain("Sign out");
        expect(html).toContain("That's everything.");
    });

    it("shows a notice the user can dismiss", () => {
        const html = render(<FeedPage now={0} actions={actions} state={state({}, {notice: "Couldn't refresh: Offline."})}/>);

        expect(html).toContain('role="status"');
        expect(html).toContain("Couldn't refresh: Offline.");
    });

    it("an empty feed says how to fill it", () => {
        const html = render(<FeedPage now={0} actions={actions} state={state({})}/>);

        expect(html).toContain("Nothing here yet. Send a link or a note");
    });
});

describe("FeedEndRow", () => {
    it("offers a retry when loading failed, and a spinner while it loads", () => {
        expect(render(<FeedEndRow end={{kind: "error", message: "Offline.", retry: "refresh"}} actions={actions} itemCount={0}/>))
            .toContain("Retry");
        expect(render(<FeedEndRow end={{kind: "loading"}} actions={actions} itemCount={3}/>)).toContain('role="progressbar"');
    });
});

describe("SignInPage", () => {
    const props = {message: null, google: null, offerOneTap: false, devSignIn: false, lastDevUser: null, onDevSignIn: () => {}};

    it("says why the user was signed out", () => {
        const html = render(<SignInPage {...props} google={new Promise(() => {})} message="Your session has expired. Sign in again."/>);

        expect(html).toContain('role="alert"');
        expect(html).toContain("Your session has expired. Sign in again.");
        expect(html).toContain("google-button");
    });

    it("offers the development sign-in, with the last user, only in a DEV_SIGN_IN build", () => {
        const dev = render(<SignInPage {...props} devSignIn={true} lastDevUser="alice"/>);
        const production = render(<SignInPage {...props} google={new Promise(() => {})}/>);

        expect(dev).toContain("Development user");
        expect(dev).toContain('value="alice"');
        expect(dev).not.toContain("google-button");
        expect(production).not.toContain("Development user");
    });

    it("says so when the build can't sign in at all", () => {
        expect(render(<SignInPage {...props}/>)).toContain("Sign-in isn't configured in this build.");
    });

    it("links the terms and the privacy policy", () => {
        const html = render(<SignInPage {...props}/>);

        expect(html).toContain('href="/terms/"');
        expect(html).toContain('href="/privacy/"');
    });
});

describe("ConfigErrorPage", () => {
    it("says the app can't start and why, escaped, with no sign-in", () => {
        const html = render(<ConfigErrorPage detail={"API_URL must be an https URL (<b>http://x</b>)"}/>);

        expect(html).toContain("Passel can't start");
        expect(html).toContain('role="alert"');
        expect(html).toContain("API_URL must be an https URL (&lt;b>http://x&lt;/b>)");
        expect(html).not.toContain("Sign in");
    });
});
