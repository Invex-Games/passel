// The handful of icons the app draws: the same strokes as the extension popup's (src/chrome-extension/src/popup/dom.ts).

const PATHS = {
    link: (
        <>
            <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/>
            <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>
        </>
    ),
    text: (
        <>
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
            <polyline points="14 2 14 8 20 8"/>
            <line x1="16" y1="13" x2="8" y2="13"/>
            <line x1="16" y1="17" x2="8" y2="17"/>
        </>
    ),
    file: (
        <>
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
            <polyline points="14 2 14 8 20 8"/>
        </>
    ),
    image: (
        <>
            <rect x="3" y="3" width="18" height="18" rx="2"/>
            <circle cx="9" cy="9" r="2"/>
            <path d="m21 15-3.09-3.09a2 2 0 0 0-2.82 0L6 21"/>
        </>
    ),
    refresh: (
        <>
            <path d="M21 12a9 9 0 1 1-2.64-6.36"/>
            <polyline points="21 3 21 9 15 9"/>
        </>
    ),
    close: (
        <>
            <line x1="18" y1="6" x2="6" y2="18"/>
            <line x1="6" y1="6" x2="18" y2="18"/>
        </>
    ),
} as const;

export type IconName = keyof typeof PATHS;

export function Icon({name}: { name: IconName }) {
    return <svg class="icon" viewBox="0 0 24 24" aria-hidden="true">{PATHS[name]}</svg>;
}
