import {useEffect, useRef, useState} from "preact/hooks";
import type {AppState} from "../controller.js";
import {feedEnd, syncLabel, type FeedEnd} from "../labels.js";
import {Icon} from "./icons.js";
import {Tile} from "./Tile.js";

// The signed-in page: the top bar, the feed newest first, and the row after the oldest item, which loads the next
// page as it scrolls into view. Read-only for now; sending and deleting come with roadmap 2.E1.

/** Older items start loading once the end of the feed is this close to being scrolled into view. */
const LOAD_MORE_MARGIN = "400px";

export interface FeedPageActions {
    refresh(): void;
    loadMore(retry: boolean): void;
    signOut(): void;
    dismissNotice(): void;
}

export function FeedPage({state, actions, now}: { state: AppState; actions: FeedPageActions; now: number }) {
    const account = state.auth.status === "signedIn" ? state.auth.account : "";
    const sync = syncLabel(state.live, state.feed.lastSyncedAt, now);

    return (
        <>
            <header class="top-bar">
                <a class="wordmark" href="/">Passel</a>
                <span class={state.live ? "sync-label live" : "sync-label"} aria-live="polite">{sync}</span>
                <button type="button" class={state.refreshing ? "icon-button spinning" : "icon-button"}
                        title="Refresh" aria-label="Refresh" disabled={state.refreshing} onClick={actions.refresh}>
                    <Icon name="refresh"/>
                </button>
                <span class="account" title={account}>{account}</span>
                <button type="button" class="text-button" onClick={actions.signOut}>Sign out</button>
            </header>

            {state.notice && (
                <div class="notice" role="status">
                    <span>{state.notice}</span>
                    <button type="button" class="icon-button" title="Dismiss" aria-label="Dismiss" onClick={actions.dismissNotice}>
                        <Icon name="close"/>
                    </button>
                </div>
            )}

            <main class="feed" aria-busy={!state.feed.loaded}>
                <ul class="feed-list" aria-label="Your links, notes and files">
                    {state.feed.packets.map(packet => <li key={packet.id}><Tile packet={packet}/></li>)}
                </ul>
                <FeedEndRow end={feedEnd(state.feed, state.loadError)} actions={actions}
                            itemCount={state.feed.packets.length}/>
            </main>
        </>
    );
}

export function FeedEndRow({end, actions, itemCount}: { end: FeedEnd; actions: FeedPageActions; itemCount: number }) {
    const row = useRef<HTMLDivElement>(null);

    // While the row shows the spinner below a loaded page, scrolling it into view loads the next page. Observing again
    // after each page reports where the row is at once, so a page too short to scroll still loads the next
    useEffect(() => {
        const element = row.current;
        if (!element || end.kind !== "loading" || itemCount === 0 || typeof IntersectionObserver === "undefined") return;

        const observer = new IntersectionObserver(entries => {
            if (entries.some(entry => entry.isIntersecting)) actions.loadMore(false);
        }, {rootMargin: LOAD_MORE_MARGIN});
        observer.observe(element);
        return () => observer.disconnect();
    }, [end.kind, itemCount]);

    return (
        <div ref={row} class={end.kind === "error" ? "feed-end error" : "feed-end"}>
            {end.kind === "loading" && <span class="spinner" role="progressbar" aria-label="Loading"/>}
            {end.kind === "message" && <span>{end.message}</span>}
            {end.kind === "error" && (
                <>
                    <span>{end.message}</span>
                    <button type="button" class="text-button"
                            onClick={() => end.retry === "refresh" ? actions.refresh() : actions.loadMore(true)}>
                        Retry
                    </button>
                </>
            )}
        </div>
    );
}

/** The current time, updated every minute, so "Synced 5 mins ago" stays true while the page is open. */
export function useMinuteClock(): number {
    const [now, setNow] = useState(() => Date.now());
    useEffect(() => {
        const timer = setInterval(() => setNow(Date.now()), 60_000);
        return () => clearInterval(timer);
    }, []);
    return now;
}
