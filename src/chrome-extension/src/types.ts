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

// A packet as shown in the feed, parsed from the API's wire format (see packets.ts).
export type PacketType = "link" | "text" | "file";

export interface Packet {
    id: string;
    type: PacketType;
    /** Link name, text title or file name. */
    title: string | null;
    /** Link URL, text body or (for files) the parcel id. */
    payload: string;
    /** Whether a file packet holds an image (file packets only). */
    isImage: boolean;
}

export interface PacketPage {
    packets: Packet[];
    nextCursor: string | null;
}

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

export interface GetPacketsMessage {
    action: "getPackets";
    cursor?: string
}

/** Deletes a packet after `delayMs`, unless a cancelDelete for it arrives first (undo). */
export interface DeletePacketMessage {
    action: "deletePacket";
    id: string;
    delayMs: number
}

export interface CancelDeleteMessage {
    action: "cancelDelete";
    id: string
}

export type ExtensionMessage =
    SignInMessage
    | SignOutMessage
    | GetAuthStateMessage
    | SendPacketMessage
    | GetPacketsMessage
    | DeletePacketMessage
    | CancelDeleteMessage;

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

/** The outcome of an API call made by the service worker. */
export interface ApiResult {
    success: boolean;
    /** A user-facing message when the call failed. */
    error?: string;
    /** The call failed because the user isn't (or is no longer) signed in. */
    signedOut?: boolean
}

export interface SendPacketResponse extends ApiResult {
    /** The created packet, if the response could be parsed. */
    packet?: Packet
}

export interface GetPacketsResponse extends ApiResult {
    page?: PacketPage
}

export interface DeletePacketResponse extends ApiResult {
    /** The deletion was undone before it was sent. */
    cancelled?: boolean
}

export interface CancelDeleteResponse {
    /** False if the deletion had already been sent (too late to undo). */
    cancelled: boolean
}

// Live updates. While the popup's feed is open it holds a port named FEED_PORT_NAME to the service
// worker, which keeps the API's event stream open and forwards its events.

export const FEED_PORT_NAME = "feed";

export type LiveEvent =
    | { kind: "resync" }
    | { kind: "created"; packet: Packet }
    | { kind: "deleted"; id: string };

export type FeedPortMessage =
    | { type: "status"; live: boolean }
    | { type: "event"; event: LiveEvent }
    | { type: "signedOut" };
