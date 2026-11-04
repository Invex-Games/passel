import type {LinkPreview, Packet} from "@passel/core";
import {useState} from "preact/hooks";
import {isLongNote, linkHost, linkHref, packetTitle} from "../labels.js";
import {Icon, type IconName} from "./icons.js";

// One item in the feed: its kind and title, then the link with its preview, the note, or (for now) a word about files.

export function Tile({packet}: { packet: Packet }) {
    const href = linkHref(packet);

    return (
        <article class="tile" data-type={packet.type}>
            <header class="tile-header">
                <Icon name={typeIcon(packet)}/>
                <h2 class="tile-title">{packetTitle(packet)}</h2>
            </header>
            {packet.type === "file"
                ? <p class="tile-note">Files can't be opened here yet.</p>
                : packet.type === "text"
                    ? <Note text={packet.payload}/>
                    : href
                        ? <Link href={href} text={packet.payload} preview={packet.preview}/>
                        // Only http(s) links are clickable; anything else is shown as it was sent
                        : <p class="tile-body">{packet.payload}</p>}
        </article>
    );
}

function Link({href, text, preview}: { href: string; text: string; preview?: LinkPreview }) {
    return (
        <>
            <a class="tile-link" href={href} target="_blank" rel="noopener noreferrer">{text}</a>
            {preview && <Preview href={href} preview={preview}/>}
        </>
    );
}

/**
 * What the linked page says about itself. The image comes straight from that site, like any image on a page: lazily,
 * without a referrer, and left out if it fails to load.
 */
function Preview({href, preview}: { href: string; preview: LinkPreview }) {
    const [imageFailed, setImageFailed] = useState(false);
    const image = preview.imageUrl && !imageFailed ? preview.imageUrl : null;

    return (
        <a class="preview" href={href} target="_blank" rel="noopener noreferrer" tabIndex={-1}>
            {image && (
                <img class="preview-image" src={image} alt="" loading="lazy" referrerPolicy="no-referrer"
                     onError={() => setImageFailed(true)}/>
            )}
            <span class="preview-text">
                {preview.title && <strong class="preview-title">{preview.title}</strong>}
                {preview.description && <span class="preview-description">{preview.description}</span>}
                <span class="preview-host">{linkHost(href)}</span>
            </span>
        </a>
    );
}

function Note({text}: { text: string }) {
    const long = isLongNote(text);
    const [expanded, setExpanded] = useState(false);
    const clamped = long && !expanded;

    return (
        <>
            <p class={clamped ? "tile-body clamped" : "tile-body"}>{text}</p>
            {long && (
                <button type="button" class="text-button" aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>
                    {expanded ? "Show less" : "Show more"}
                </button>
            )}
        </>
    );
}

function typeIcon(packet: Packet): IconName {
    switch (packet.type) {
        case "link":
            return "link";
        case "text":
            return "text";
        case "file":
            return packet.isImage ? "image" : "file";
    }
}
