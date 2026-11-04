import {useEffect, useState} from "preact/hooks";
import type {AppController, AppState} from "../controller.js";
import type {GoogleSignIn} from "../google.js";
import {FeedPage, useMinuteClock} from "./FeedPage.js";
import {SignInPage} from "./SignInPage.js";

export interface AppProps {
    controller: AppController;
    /** Loads Google sign-in the first time the sign-in page needs it; null when this build has no Google client id. */
    google: (() => Promise<GoogleSignIn>) | null;
    devSignIn: boolean;
    /** Whether One Tap may be offered as soon as the sign-in page opens. */
    offerOneTap(): boolean;
    lastDevUser(): string | null;
}

export function App(props: AppProps) {
    const state = useAppState(props.controller);
    const now = useMinuteClock();
    const {controller} = props;

    if (state.auth.status === "signedIn") {
        return <FeedPage state={state} now={now} actions={{
            refresh: () => void controller.refresh(true),
            loadMore: retry => void controller.loadMore(retry),
            signOut: () => void controller.signOut(),
            dismissNotice: () => controller.dismissNotice(),
        }}/>;
    }

    return <SignInPage
        message={state.auth.message}
        google={props.google ? props.google() : null}
        offerOneTap={props.offerOneTap()}
        devSignIn={props.devSignIn}
        lastDevUser={props.lastDevUser()}
        onDevSignIn={user => void controller.signInAsDevUser(user)}/>;
}

function useAppState(controller: AppController): AppState {
    const [state, setState] = useState(controller.state);
    useEffect(() => {
        setState(controller.state);
        return controller.subscribe(setState);
    }, [controller]);
    return state;
}
