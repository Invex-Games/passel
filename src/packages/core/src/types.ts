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
    /**
     * What a link's page says about itself, once the API has fetched it (links only). Absent until then, and for
     * good when the page gave nothing; it arrives with a packet-updated event, and the list has it from then on.
     */
    preview?: LinkPreview;
}

/** A link's preview, as parsed: at least one of the three is set. */
export interface LinkPreview {
    title: string | null;
    description: string | null;
    /** An absolute http(s) URL of an image on the linked site; the parser drops anything else. */
    imageUrl: string | null;
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
    action: "signIn";
    /** Signs in as this development user instead of through Google (DEV_SIGN_IN builds only). */
    devUser?: string
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
    /** The signed-in account as shown to the user: an email, or "<user> (dev)" for a development user. */
    email?: string;
    /** Signed out, in a DEV_SIGN_IN build: the development user last signed in as, to offer again. */
    lastDevUser?: string
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
    /** A packet changed after it was created (a link got its preview): it is replaced where it is listed, never added. */
    | { kind: "updated"; packet: Packet }
    | { kind: "deleted"; id: string };

export type FeedPortMessage =
    | { type: "status"; live: boolean }
    | { type: "event"; event: LiveEvent }
    | { type: "signedOut" };

/**
 * From the service worker to every open page (the popup, the side panel), signed in or not. `sessionChanged`:
 * someone signed in or out, from any page, shortcut or menu, so each page asks for the auth state again.
 */
export type PageMessage = { type: "sessionChanged" };

// The feed as a client caches it between runs: the first page and the cursor after it.

export interface CachedFeed {
    packets: Packet[];
    nextCursor: string | null;
    /** When the feed was last fetched (ms since the epoch). */
    lastSyncedAt: number | null;
}
