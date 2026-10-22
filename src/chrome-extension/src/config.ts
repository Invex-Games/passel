declare const __CONFIG__: {
    API_URL: string;
    GOOGLE_CLIENT_ID: string;
    DEV_SIGN_IN: boolean;
};

export const API_URL = __CONFIG__.API_URL;
export const GOOGLE_CLIENT_ID = __CONFIG__.GOOGLE_CLIENT_ID;

/**
 * Whether the popup offers signing in as a development user, through the API's Dev scheme instead of
 * Google. Only a development build (`--dev`) with DEV_SIGN_IN=true has it; a minified build never does.
 */
export const DEV_SIGN_IN = __CONFIG__.DEV_SIGN_IN;
