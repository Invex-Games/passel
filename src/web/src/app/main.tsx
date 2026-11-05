import {PacketsApi} from "@passel/core";
import {render} from "preact";
import {type AppConfig, ConfigError, loadConfig} from "./config.js";
import {AppController} from "./controller.js";
import {DEV_SIGN_IN, LIVE_RELOAD} from "./flags.js";
import {loadGoogleSignIn, type GoogleSignIn} from "./google.js";
import {Session} from "./session.js";
import {App} from "./views/App.js";
import {ConfigErrorPage} from "./views/ConfigErrorPage.js";

// The web app's entry point: the configuration from /app/config.json first, then the real storage, API, Google
// sign-in and page visibility, handed to the controller, and the views rendered into #app.

const root = document.getElementById("app");

function start(config: AppConfig) {
    const session = new Session({
        clientId: config.googleClientId,
        devSignIn: DEV_SIGN_IN,
        tabStore: sessionStorage,
        browserStore: localStorage,
    });

    // A Google ID token can't be renewed without the user (One Tap at best), so a 401 ends the session: the sign-in
    // page then says so and offers One Tap
    const api = new PacketsApi({endpoint: config.apiUrl, getToken: () => session.token()});

    // Every credential Google hands back must carry this tab's nonce
    const nonce = crypto.randomUUID();
    let google: Promise<GoogleSignIn> | null = null;

    function loadGoogle(): Promise<GoogleSignIn> {
        if (!google) {
            const loading = loadGoogleSignIn(config.googleClientId, nonce, credential => void controller.signInWithGoogle(credential, nonce));
            // A failed load is retried the next time the sign-in page opens
            loading.catch(() => {
                if (google === loading) google = null;
            });
            google = loading;
        }
        return google;
    }

    const controller = new AppController({session, api});

    document.addEventListener("visibilitychange", () => controller.setVisible(document.visibilityState === "visible"));
    controller.setVisible(document.visibilityState === "visible");

    // Before the first render, so a tab that is already signed in opens on its feed rather than the sign-in page
    controller.start();

    if (root) {
        root.replaceChildren();
        render(
            <App controller={controller}
                 google={config.googleClientId ? loadGoogle : null}
                 devSignIn={DEV_SIGN_IN}
                 offerOneTap={() => session.returning}
                 lastDevUser={() => session.lastDevUser}/>,
            root,
        );
    }

    // After a sign-out, One Tap asks before signing the same account in again
    let signedIn = controller.state.auth.status === "signedIn";
    controller.subscribe(state => {
        const nowSignedIn = state.auth.status === "signedIn";
        if (signedIn && !nowSignedIn && !session.returning) google?.then(signIn => signIn.disableAutoSelect()).catch(() => {});
        signedIn = nowSignedIn;
    });
}

if (LIVE_RELOAD) new EventSource("/esbuild").addEventListener("change", () => location.reload());

// Nothing starts without a valid configuration: no session, no API client and no Google script, only the reason
let config: AppConfig | null = null;
try {
    config = await loadConfig(fetch, {devSignIn: DEV_SIGN_IN});
} catch (err) {
    const detail = err instanceof ConfigError ? err.message : "the configuration could not be read";
    console.error("Passel web app configuration:", err);
    if (root) {
        root.replaceChildren();
        render(<ConfigErrorPage detail={detail}/>, root);
    }
}

if (config) start(config);
