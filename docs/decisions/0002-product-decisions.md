# ADR 0002: Ordering, vocabulary, web app stack, parcel storage and hosting

**Status:** accepted, October 2026. **Decides:** roadmap package 0.F1, the product decisions the evaluation left open, plus the hosting choice that package 0.A2 needs.

## Context

The evaluation found the clients disagreeing on the order of the feed ([UX](../evaluation/ux.md), finding 4) and leaking internal vocabulary into the screens (finding 5), compared stacks for the web app ([Web App Plan](../evaluation/web-app.md)) and storage backends for parcels ([Feature Ideas](../evaluation/features.md)), and noted that nothing deploys the API yet ([Developer Experience](../evaluation/developer-experience.md), finding 1). Each of those gates later work: the share target, the landing, privacy and terms pages, parcels, the tile redesigns, the web app and the deployment pipeline. The recommendations of those chapters are adopted as they stand.

## Decisions

- **Ordering: newest at the top, everywhere.** The feed is a feed, not a chat. The extension already shows newest first; the mobile app flips to match, so the same item sits in the same place on every device, pull-to-refresh is the usual pull down at the top, and older items load at the bottom. The web app follows.
- **Vocabulary: one verb, "Send"; the nouns are link, note and file.** Screens, notifications and store listings use those words; "item" or "items" where the type is not known. "Packet" and "parcel" stay as code and API terms, and the API contract does not change.
- **Web app: a TypeScript progressive web app sharing a core package with the extension.** The extension's chrome-free modules (feed state, parsing, server-sent events, types) become that package first (roadmap package 1.E1), the web app consumes it (1.E2), and the extension becomes another consumer. Flutter Web and Blazor were compared and set aside: the web platform's own strengths (selection, search engines, share target, push, installability) matter for this app, and the shared code with the extension is the larger saving.
- **Parcels: Azure Blob Storage behind an `IParcelStore` interface**, with Azurite running locally under the AppHost. The interface (put, get as a stream, delete, later a signed URL) keeps an S3-compatible implementation to about a day's work if hosting ever moves.
- **Hosting: Azure.** The build already uses Azure (Key Vault), the parcel store is Azure Blob, and Aspire's Azure integrations cover the local run. Package 0.A2 chooses the concrete compute and database services and documents the secrets.

## Consequences

- The mobile feed changes direction, which also changes the pull-to-refresh gesture; the hint text and the app bar behaviour follow.
- Visible strings in both clients change; later packages keep to the vocabulary, and the privacy and terms pages and the store listings (1.E3, 2.A1) use it from the start.
- The web app work can start with the core extraction; nothing in the extension's behaviour changes until it consumes the package.
- Parcels and the deployment pipeline can be designed against Azure services without a further decision.
- Should hosting leave Azure, the parcel store swaps behind its interface and the deployment target changes; nothing in the clients depends on either.
