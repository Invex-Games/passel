// Google Identity Services: the "Sign in with Google" button and One Tap, which hand the page an ID token issued for
// the web client id, the API's main audience. The script is loaded only when the sign-in page needs it.
// https://developers.google.com/identity/gsi/web/reference/js-reference

const SCRIPT_URL = "https://accounts.google.com/gsi/client";

interface CredentialResponse {
    credential: string;
}

interface GoogleAccountsId {
    initialize(config: {
        client_id: string;
        callback: (response: CredentialResponse) => void;
        nonce?: string;
        auto_select?: boolean;
        context?: "signin" | "signup" | "use";
        use_fedcm_for_button?: boolean;
        cancel_on_tap_outside?: boolean;
    }): void;

    renderButton(parent: HTMLElement, options: {
        type?: "standard" | "icon";
        theme?: "outline" | "filled_blue" | "filled_black";
        size?: "large" | "medium" | "small";
        text?: "signin_with" | "signup_with" | "continue_with" | "signin";
        shape?: "rectangular" | "pill" | "circle" | "square";
        width?: number;
    }): void;

    prompt(): void;

    cancel(): void;

    disableAutoSelect(): void;
}

declare global {
    interface Window {
        google?: { accounts: { id: GoogleAccountsId } };
    }
}

/** What the rest of the app uses of Google Identity Services. */
export interface GoogleSignIn {
    /** Draws the sign-in button into `parent`. */
    renderButton(parent: HTMLElement): void;

    /** Shows One Tap, which signs a returning user straight back in where Google allows it. */
    prompt(): void;

    /** Closes One Tap if it is showing. */
    cancel(): void;

    /** After a sign-out, One Tap asks before signing the same account in again. */
    disableAutoSelect(): void;
}

let loading: Promise<GoogleAccountsId> | null = null;

function loadScript(): Promise<GoogleAccountsId> {
    loading ??= new Promise<GoogleAccountsId>((resolve, reject) => {
        const script = document.createElement("script");
        script.src = SCRIPT_URL;
        script.async = true;
        script.onload = () => window.google
            ? resolve(window.google.accounts.id)
            : reject(new Error("Google sign-in didn't start."));
        script.onerror = () => {
            loading = null;
            script.remove();
            reject(new Error("Couldn't reach Google to sign in. Check your connection and try again."));
        };
        document.head.append(script);
    });
    return loading;
}

/**
 * Loads Google Identity Services and sets it up for `clientId`. Every credential it hands back carries `nonce`,
 * which the session checks, and goes to `onCredential`.
 */
export async function loadGoogleSignIn(
    clientId: string,
    nonce: string,
    onCredential: (credential: string) => void,
): Promise<GoogleSignIn> {
    const id = await loadScript();

    id.initialize({
        client_id: clientId,
        callback: response => onCredential(response.credential),
        nonce,
        // A returning user who signed in before and has one Google session is signed in without a click
        auto_select: true,
        context: "signin",
        use_fedcm_for_button: true,
    });

    return {
        renderButton: parent => id.renderButton(parent, {
            type: "standard", theme: "outline", size: "large", text: "signin_with", shape: "pill",
        }),
        prompt: () => id.prompt(),
        cancel: () => id.cancel(),
        disableAutoSelect: () => id.disableAutoSelect(),
    };
}
