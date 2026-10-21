// Request bodies for POST /api/packets. `$type` must be the first property, and
// nullable fields must still be sent (as null) because the server requires them.
// File packets aren't supported by the server yet, so the extension only sends text and links.

export interface TextPacketData {
    $type: "Text";
    title: string | null;
    body: string;
}

export interface LinkPacketData {
    $type: "Link";
    name: string | null;
    url: string;
}

export type PacketData = TextPacketData | LinkPacketData;

// Error body returned by the API for failed requests (RFC 9457 problem details).
export interface ProblemDetails {
    title?: string;
    detail?: string;
    status?: number;
    errors?: Record<string, string[]>;
}

// Messages sent from the popup to the service worker. All API calls and sign-in
// run in the worker so they survive the popup closing (e.g. when a sign-in window opens).

export interface SignInMessage {
    action: "signIn"
}

export interface SignOutMessage {
    action: "signOut"
}

export interface GetAuthStateMessage {
    action: "getAuthState"
}

export interface SendPacketMessage {
    action: "sendPacket";
    data: PacketData
}

export type ExtensionMessage = SignInMessage | SignOutMessage | GetAuthStateMessage | SendPacketMessage;

export interface AuthStateResponse {
    signedIn: boolean;
    email?: string
}

export interface SignInResponse {
    success: boolean;
    email?: string;
    error?: string
}

export interface SignOutResponse {
    success: boolean
}

export interface SendPacketResponse {
    success: boolean;
    error?: string;
    signedOut?: boolean
}
