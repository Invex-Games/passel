import {useEffect, useRef, useState} from "preact/hooks";
import type {GoogleSignIn} from "../google.js";

// The signed-out page: the Google button, and in a development build with DEV_SIGN_IN a field for signing in as a
// development user through the API's Dev scheme. It says why the user was signed out when they didn't ask to be.

export interface SignInPageProps {
    /** Why the user is signed out, if it wasn't their choice, or why the last sign-in failed. */
    message: string | null;
    /** Google sign-in, once loaded; null when this build has no Google client id. */
    google: Promise<GoogleSignIn> | null;
    /** Whether to offer One Tap straight away: the user signed in here before and hasn't signed out. */
    offerOneTap: boolean;
    devSignIn: boolean;
    lastDevUser: string | null;
    onDevSignIn(user: string): void;
}

export function SignInPage(props: SignInPageProps) {
    return (
        <main class="sign-in">
            <a class="wordmark" href="/">Passel</a>
            <h1>Your links and notes, on every device</h1>
            <p class="sign-in-lead">Sign in to see what you've sent from your other devices.</p>
            {props.message && <p class="sign-in-message" role="alert">{props.message}</p>}
            {props.google && <GoogleButton google={props.google} offerOneTap={props.offerOneTap}/>}
            {props.devSignIn && <DevSignIn lastDevUser={props.lastDevUser} onSignIn={props.onDevSignIn}/>}
            {!props.google && !props.devSignIn && (
                <p class="sign-in-message" role="alert">Sign-in isn't configured in this build.</p>
            )}
            <p class="sign-in-legal">
                By signing in you agree to the <a href="/terms/">terms</a>; the <a href="/privacy/">privacy
                policy</a> says what Passel keeps.
            </p>
        </main>
    );
}

function GoogleButton({google, offerOneTap}: { google: Promise<GoogleSignIn>; offerOneTap: boolean }) {
    const container = useRef<HTMLDivElement>(null);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        let cancelled = false;
        let loaded: GoogleSignIn | null = null;

        google
            .then(signIn => {
                if (cancelled || !container.current) return;
                loaded = signIn;
                signIn.renderButton(container.current);
                if (offerOneTap) signIn.prompt();
            })
            .catch(err => {
                if (!cancelled) setError((err as Error).message);
            });

        return () => {
            cancelled = true;
            loaded?.cancel();
        };
    }, [google]);

    return (
        <>
            <div class="google-button" ref={container}/>
            {error && <p class="sign-in-message" role="alert">{error}</p>}
        </>
    );
}

function DevSignIn({lastDevUser, onSignIn}: { lastDevUser: string | null; onSignIn(user: string): void }) {
    return (
        <form class="dev-sign-in" onSubmit={event => {
            event.preventDefault();
            // Read from the field itself, so a value set without an input event (autofill) still counts
            onSignIn(String(new FormData(event.currentTarget).get("dev-user") ?? ""));
        }}>
            <label for="dev-user">Development user</label>
            <div class="dev-sign-in-row">
                <input id="dev-user" name="dev-user" autocomplete="off" spellcheck={false} placeholder="alice"
                       defaultValue={lastDevUser ?? ""}/>
                <button type="submit" class="filled-button">Sign in</button>
            </div>
            <p class="dev-sign-in-hint">Signs in through the API's Development-only Dev scheme. Each name is its own account.</p>
        </form>
    );
}
