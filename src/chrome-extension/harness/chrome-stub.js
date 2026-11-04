// A stand-in for the chrome.* APIs the popup uses, with canned data, so the real popup.js runs in a plain
// browser tab (see serve.mjs). The service worker hands the popup already-parsed packets, so the samples
// here have the popup's own shape: {id, type, title, payload, isImage}.
(function () {
    const params = new URLSearchParams(location.search);
    const state = params.get("state");
    const signedOut = state === "signedout";
    const empty = state === "empty";
    const lastDevUser = params.get("lastDevUser") || "";
    let email = "declan@example.com";

    const samplePackets = [
        {id: "k9Qm2xLp7a", type: "link", title: "ASP.NET Core server-sent events | Microsoft Learn", payload: "https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses?view=aspnetcore-10.0#server-sent-events", isImage: false},
        {id: "b3Tz8nQw1c", type: "text", title: null, payload: "0423 118 992", isImage: false},
        {id: "c7Hd4vRs9e", type: "text", title: "Grocery list", payload: "Milk\nEggs\nSourdough\nOat milk (barista)\nParmesan\nCoffee beans - the Ethiopian one\nBananas\nSpinach\nTomatoes\nGarlic", isImage: false},
        {id: "d2Jf6kWx3g", type: "link", title: null, payload: "https://github.com/Invex-Games/atom", isImage: false},
        {id: "e5Lg8mYz4h", type: "file", title: "IMG_20261021_183322.jpg", payload: "p1", isImage: true},
        {id: "f8Nh1pAb5j", type: "text", title: null, payload: "Remember to ask about the parking permit renewal before the end of the month, and bring the old permit.", isImage: false},
        {id: "g1Pj3rCd6k", type: "link", title: "Passel", payload: "https://passel.link", isImage: false},
    ];

    const storageArea = store => ({
        get: async key => (key in store ? {[key]: store[key]} : {}),
        set: async obj => Object.assign(store, obj),
        remove: async keys => {
            for (const key of [].concat(keys)) delete store[key];
        },
    });

    // Deletions wait out the undo window as in the service worker, and a cancel makes the pending one answer
    // that it was cancelled.
    const pendingDeletes = new Map();

    let port;
    function makePort(name) {
        const onMessage = [];
        const onDisconnect = [];
        const p = {
            name,
            onMessage: {addListener: f => onMessage.push(f)},
            onDisconnect: {addListener: f => onDisconnect.push(f)},
            postMessage() {
            },
            disconnect() {
                onDisconnect.forEach(f => f());
            },
            _emit(m) {
                onMessage.forEach(f => f(m));
            },
        };
        setTimeout(() => p._emit({type: "status", live: true}), 300);
        setTimeout(() => p._emit({type: "event", event: {kind: "resync"}}), 400);
        return p;
    }

    // From the browser console: passel.created({...}) makes a packet arrive live, passel.emit(message) sends
    // any feed port message.
    window.passel = {
        emit(m) {
            port && port._emit(m);
        },
        created(packet) {
            port && port._emit({type: "event", event: {kind: "created", packet}});
        },
    };

    window.chrome = {
        runtime: {
            id: "harness",
            sendMessage: async message => {
                await new Promise(r => setTimeout(r, 150));
                switch (message.action) {
                    case "getAuthState":
                        if (!signedOut) return {signedIn: true, email};
                        return lastDevUser ? {signedIn: false, lastDevUser} : {signedIn: false};
                    case "signIn": {
                        if (message.devUser !== undefined) {
                            const user = message.devUser.trim();
                            if (!/^[A-Za-z0-9._@+-]{1,255}$/.test(user)) {
                                return {success: false, error: "A development user must be 1 to 255 letters, digits or -._@+ characters."};
                            }
                            email = `${user} (dev)`;
                        }
                        return {success: true, email};
                    }
                    case "signOut":
                        return {success: true};
                    case "getPackets":
                        return {success: true, page: {packets: empty ? [] : samplePackets, nextCursor: null}};
                    case "sendPacket": {
                        const d = message.data;
                        const id = `new${Math.random().toString(36).slice(2, 9)}`;
                        const packet = d.$type === "Link"
                            ? {id, type: "link", title: d.name, payload: d.url, isImage: false}
                            : {id, type: "text", title: d.title, payload: d.body, isImage: false};
                        return {success: true, packet};
                    }
                    case "deletePacket":
                        return new Promise(resolve => {
                            const timer = setTimeout(() => {
                                pendingDeletes.delete(message.id);
                                resolve({success: true});
                            }, message.delayMs);
                            pendingDeletes.set(message.id, {timer, resolve});
                        });
                    case "cancelDelete": {
                        const pending = pendingDeletes.get(message.id);
                        if (!pending) return {cancelled: false};
                        clearTimeout(pending.timer);
                        pendingDeletes.delete(message.id);
                        pending.resolve({success: false, cancelled: true});
                        return {cancelled: true};
                    }
                }
                return {success: false, error: "unknown action"};
            },
            connect: ({name}) => (port = makePort(name)),
            // The service worker's word that the session changed elsewhere; nothing here sends it.
            onMessage: {
                addListener() {
                },
            },
        },
        storage: {local: storageArea({}), session: storageArea({})},
        // The side panel asks for the tabs permission before sending the page; here it's always granted.
        permissions: {request: async () => true},
        // Opening the side panel from the popup logs instead; a browser tab ignores the window.close() that follows.
        sidePanel: {open: async options => console.log("open side panel", options)},
        windows: {getCurrent: async () => ({id: 1})},
        tabs: {
            query: async () => [{url: "https://docs.flutter.dev/ui/widgets", title: "Widgets | Flutter"}],
            create: async ({url}) => console.log("open", url),
        },
    };
})();
