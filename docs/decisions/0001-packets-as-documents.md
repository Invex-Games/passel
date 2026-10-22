# ADR 0001: Packets are plain documents

**Status:** accepted, October 2026. **Decides:** the storage model for packets, the open question from the [codebase health](../evaluation/codebase-health.md#decision-storage-model-for-packets) chapter of the evaluation.

## Context

Packets were event-sourced: each packet was a Marten event stream (`TextPacketCreated`, `LinkPacketCreated`, `FilePacketCreated`, `PacketDeleted`) with `PacketAggregate` kept as an inline snapshot document, which the reads queried. Nothing read the history. The consequences, from the evaluation:

- A delete only removed the snapshot. The body or URL stayed in `mt_events` forever, in every backup and in any operator query, so "delete" never erased content ([security](../evaluation/security.md), finding 1) and the event table grew without bound ([performance](../evaluation/performance.md), finding 6).
- Every create wrote the event and the snapshot, and read the snapshot back; a delete appended an event and ran the projection. Bodies were stored twice.
- Each future feature on a packet (previews, pins, parcels, trash) meant an event type plus projection code, and the runtime code generation and rebuild machinery that come with projections.

The options were to keep event sourcing with archiving and a purge job, to store packets as plain documents, or plain documents plus a per-user change log for resumable sync.

## Decision

Packets are plain Marten documents. One `Packet` document holds `Id` (a version 7 `Guid`, so id order is creation order), `UserId`, `CreatedAt` and the `Content` union. Creating is one upsert, deleting is one delete, and the feed is a LINQ query served by the composite index on `UserId` and `Id`. The event types, the aggregate and the inline projection are gone.

`CreatedAt` is returned by the list and create endpoints and in the `packet-created` event, so clients can show times and day headers.

A delete erases the packet: the document was its only record. Trash and restore, when they come (roadmap package 2.B3), will mark documents deleted and purge them later rather than bring back an event history; Marten's soft-delete support fits that, so no `DeletedAt` field is reserved now.

The per-user change log is deferred until resumable streams or `?since=` delta sync are wanted (roadmap package 2.B4). It is a small table written in the same transaction as the document, and the document model does not change for it.

## Consequences

- Deletes are true deletes, which the privacy findings asked for, and storage holds each packet once.
- The write path is simpler and faster: one statement per create or delete, and no projection rebuilds or generated projection code.
- Features become fields on the document rather than events.
- There is no audit trail of packet changes. Nothing used it, and the change log can provide a bounded one when sync needs it.
- A database created before this change keeps its event-sourcing tables (`mt_doc_packetaggregate`, `mt_events`, `mt_streams`). Nothing reads them and they can be dropped. Old packets are not migrated: no production data existed when the change landed.
- The ids, the public id encoding, the feed query and its index, the quota aggregate and the live-update flow are unchanged apart from the table they read, so the clients needed no change beyond tolerating the new `createdAt` field, which both already did.
