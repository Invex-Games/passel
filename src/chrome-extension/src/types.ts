// Request bodies for POST /api/packets. `$type` must be the first property, and
// nullable fields must still be sent (as null) because the server requires them.

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

export interface FilePacketData {
    $type: "File";
    fileName: string | null;
    contentType: string;
    payload: string;
}

export type PacketData = TextPacketData | LinkPacketData | FilePacketData;

export interface SignInMessage {
    action: "signIn"
}

export interface SignOutMessage {
    action: "signOut"
}

export interface GetAuthStateMessage {
    action: "getAuthState"
}

export type ExtensionMessage = SignInMessage | SignOutMessage | GetAuthStateMessage;

export interface SignInResponse {
    success: boolean;
    token?: string;
    error?: string
}

export interface AuthStateResponse {
    token?: string
}