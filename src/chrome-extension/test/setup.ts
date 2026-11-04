// build.mjs defines __CONFIG__ when it bundles (see src/config.ts); the tests run the sources unbundled, so
// they get fixed values here. A test that needs other values (DEV_SIGN_IN, no client id) mocks ../src/config.js.
Object.assign(globalThis, {
    __CONFIG__: {
        API_URL: "https://api.passel.test/api/packets?api-version=1",
        GOOGLE_CLIENT_ID: "client-1.apps.googleusercontent.com",
        DEV_SIGN_IN: false,
    },
});
