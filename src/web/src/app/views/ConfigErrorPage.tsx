// Shown instead of the app when its configuration is missing or not valid (config.ts), so the app never starts
// half-working against the wrong API or with a client id Google would refuse.

export function ConfigErrorPage({detail}: { detail: string }) {
    return (
        <main class="sign-in">
            <a class="wordmark" href="/">Passel</a>
            <h1>Passel can't start</h1>
            <p class="sign-in-message" role="alert">
                This copy of the web app isn't set up correctly: {detail}. Whoever runs it needs to fix its
                configuration.
            </p>
        </main>
    );
}
