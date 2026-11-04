import {vi} from "vitest";

// A stand-in for the parts of `chrome.*` the extension uses: storage that keeps what's written to it, events
// whose listeners a test can fire, and vi.fn()s for everything else, so a test can answer them and check how
// they were called. installChrome() puts a fresh one on globalThis for each test (vitest.config.ts unstubs it).

type Listener = (...args: any[]) => any;

/** A chrome.events.Event a test can fire. */
export class FakeEvent<T extends Listener = Listener> {
    readonly listeners: T[] = [];

    addListener = (listener: T): void => {
        this.listeners.push(listener);
    };

    removeListener = (listener: T): void => {
        const index = this.listeners.indexOf(listener);
        if (index >= 0) this.listeners.splice(index, 1);
    };

    hasListener = (listener: T): boolean => this.listeners.includes(listener);

    /** Calls every listener, as Chrome would, and returns what each returned. */
    fire(...args: Parameters<T>): ReturnType<T>[] {
        return this.listeners.map(listener => listener(...args));
    }
}

/** A chrome.storage area backed by a plain object, copying values in and out as Chrome does. */
export class FakeStorageArea {
    data: Record<string, unknown> = {};

    get = vi.fn(async (keys?: string | string[] | null): Promise<Record<string, unknown>> => {
        const wanted = keys == null ? Object.keys(this.data) : ([] as string[]).concat(keys);
        const result: Record<string, unknown> = {};
        for (const key of wanted) {
            if (key in this.data) result[key] = structuredClone(this.data[key]);
        }
        return result;
    });

    set = vi.fn(async (items: Record<string, unknown>): Promise<void> => {
        Object.assign(this.data, structuredClone(items));
    });

    remove = vi.fn(async (keys: string | string[]): Promise<void> => {
        for (const key of ([] as string[]).concat(keys)) delete this.data[key];
    });
}

export const EXTENSION_ID = "aikjdmilnbdacmaeahelifppjjgppemj";
export const REDIRECT_URL = `https://${EXTENSION_ID}.chromiumapp.org/`;

export function createChrome() {
    return {
        runtime: {
            id: EXTENSION_ID,
            onInstalled: new FakeEvent(),
            onMessage: new FakeEvent(),
            onConnect: new FakeEvent(),
            sendMessage: vi.fn(async (_message: unknown): Promise<unknown> => undefined),
            connect: vi.fn((_info: { name: string }): unknown => fakePort()),
        },
        storage: {
            local: new FakeStorageArea(),
            session: new FakeStorageArea(),
        },
        identity: {
            getRedirectURL: vi.fn(() => REDIRECT_URL),
            launchWebAuthFlow: vi.fn(async (_details: { url: string; interactive?: boolean }): Promise<string | undefined> => undefined),
        },
        contextMenus: {
            create: vi.fn(),
            removeAll: vi.fn(async () => {
            }),
            onClicked: new FakeEvent(),
        },
        action: {
            setBadgeText: vi.fn(async (_details: { text: string }) => {
            }),
            setBadgeBackgroundColor: vi.fn(async (_details: { color: string }) => {
            }),
        },
        commands: {
            onCommand: new FakeEvent(),
        },
        omnibox: {
            onInputStarted: new FakeEvent(),
            onInputChanged: new FakeEvent(),
            onInputEntered: new FakeEvent(),
            setDefaultSuggestion: vi.fn(async (_suggestion: { description: string }) => {
            }),
        },
        sidePanel: {
            open: vi.fn(async (_options: { windowId?: number; tabId?: number }) => {
            }),
        },
        scripting: {
            executeScript: vi.fn(async (_injection: unknown): Promise<{ result?: unknown }[]> => []),
        },
        tabs: {
            query: vi.fn(async (_query: unknown): Promise<Partial<chrome.tabs.Tab>[]> => []),
            create: vi.fn(async (_properties: { url?: string }) => ({})),
        },
        permissions: {
            request: vi.fn(async (_permissions: chrome.permissions.Permissions) => false),
        },
        windows: {
            getCurrent: vi.fn(async () => ({id: 7})),
        },
    };
}

export type FakeChrome = ReturnType<typeof createChrome>;

/** Puts a fresh fake on globalThis.chrome and returns it. */
export function installChrome(): FakeChrome {
    const fake = createChrome();
    vi.stubGlobal("chrome", fake);
    return fake;
}

/** A chrome.runtime.Port; disconnect() fires onDisconnect, as the other end would see it. */
export function fakePort(name = "feed", senderId = EXTENSION_ID) {
    const port = {
        name,
        sender: {id: senderId},
        onMessage: new FakeEvent(),
        onDisconnect: new FakeEvent(),
        postMessage: vi.fn((_message: unknown) => {
        }),
        disconnect: vi.fn(() => {
            port.onDisconnect.fire(port);
        }),
    };
    return port;
}

export type FakePort = ReturnType<typeof fakePort>;

/** Treats a fake as the chrome type it stands in for, at the call into the code under test. */
export function asChrome<T>(fake: unknown): T {
    return fake as T;
}

/** Lets pending promise callbacks run (several rounds, for chains of awaits). */
export async function flushPromises(rounds = 10): Promise<void> {
    for (let i = 0; i < rounds; i++) await Promise.resolve();
}
