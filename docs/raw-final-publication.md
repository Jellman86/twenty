# Durable publication of a raw final result

Implementation under validation, 27 October 2026. This extends the [permanent final claim](raw-study-final.md) with an opt-in `raw-final-publication-v1` boundary for new claims. The candidate, claim, request and trial ids remain the original ones. Core accounting and selection rules are unchanged.

## Capacity before spending

New final admission reserves approximately 34 MiB for the pair before committing its claim: two maximum 8 MiB reports, two maximum staging envelopes and ordinary bookkeeping allowance. Reservation rows are committed with the claim and original pair. Insufficient room refuses `raw-study-publication-capacity` with no claim, final rows or requests written. Missing/extra/mismatched reservation rows fail closed.

Research artifact writers, including both dataset-import paths, include outstanding reservations in their existing 256 MiB quota checks. An existing immutable input retry consumes no new file space. Each final retains both its staged envelope and published report. Pending publication conservatively retains the full reservation in addition to materialized files; success, scientific failure or explicit stop releases its unused reservation atomically with family state. Both immutable copies continue counting toward storage after publication.

This is application headroom, not physical disk allocation. Files placed outside those writers, other modules' database writes, SQLite sidecars and physical IO failure can still exhaust space. Bytes lost before a complete durable stage require an exact backup; they cannot be recalculated through recovery.

## Verified publication boundary

After calculation and full receipt verification, the worker records a `publishing` row with immutable stage/report hashes, lengths, verification time and original worker label. This marker precedes stage IO. `runs/<trial-id>/raw-study-publication.stage` is one atomic flushed envelope: a four-byte little-endian header length, at most 4 KiB of header JSON, then the exact report bytes. The header binds study, claim, registry/trial/request, plan/dataset/scientific identities and the original verified result. This framing avoids base64 expansion and a separate manifest/body commit gap.

Publication writes the same report bytes through the existing immutable report path, then commits successful trial/parent/screen state and headroom together. A conflicting target is refused without overwriting it. A publication fault leaves the original verified marker pending and its unstarted partner waiting; calculation/verification failures before the marker retain the existing permanent-failure policy. Explicit stop remains terminal, retains its evidence and spent claim, and prevents recovery from reopening the family.

## Replay and operator recovery

Startup queues each pending publication, in original queue order, for one bounded attempt on the shared worker. The constructor does not replay ledgers. A failed attempt waits for explicit repair/recovery or another restart; it is not hot-looped. Other registered families, including universe work, can proceed while that family's partner waits. Repeated identical errors retain the same journal state rather than appending duplicate events.

`POST /api/research/raw-studies/{id}/recover-publication` uses the original `{ requestId, planSha256 }`. UUID case aliases are accepted; another final UUID cannot authorise recovery. The same-origin/header guard, one computation slot and two-minute verification deadline apply. An already terminal/published request is a metadata replay, including after a code change.

For pending work, matching scientific assemblies and accounting build/runtime are required. Recovery checks marker, envelope, header, report role and every receipt's original plan/trial/strategy binding before any dataset read. It then uses `RawStudyDevelopmentRun.Verify`: frozen requests are reconstructed and original ledgers replayed for exact comparison. This is verification of the recorded calculation, not another candidate attempt or a choice between results. The worker's `Run`/`RunStage` path is never used for that pending row. Exact bytes are published; the original partner can subsequently calculate once.

Missing, unreadable, corrupt or mismatched stages, unavailable originals, changed environments and publication faults remain pending with specific errors. Restoring exact original bytes can make recovery possible. Legacy parents omit the protocol field; pre-marker or legacy orphan reports are not adopted. The workbench shows expected report/stage hashes and provides recovery against the recorded claim, without another final-spend acknowledgement. Final report reads enforce the report era associated with the new publication protocol while historical final-v1 reads remain supported.

## Validation and limits

TDD initially demonstrates pre-claim quota failure and loss of the partner/result on a deterministic SQLite success-transaction fault. Additional red cases pin wrong report/receipt bindings reaching a dataset read, a blocked universe queue, historical report-era substitution and duplicate failed-retry journal growth. Focused tests cover exact publication/restoration, missing/corrupt/swapped/coherently forged evidence, existing-file conflict, constructor queuing, unavailable non-dataset originals, cancellation, clock rollback, single-worker ownership, headroom protection and legacy-orphan refusal. Authored browser checks cover the original UUID, errors/retry, fixed hashes, older-build controls and 390-pixel layout. Final suites, real-process/API acceptance and deployment are recorded separately when completed.

The design review request to Big Pickle failed at its provider's free usage limit before inspection. No independent design/implementation verdict is claimed. Inputs are authored. Hash binding is not a vendor signature or an audit of real data quality. Retained stages have a storage cost; loss before durable staging, cross-build verification, independent durable/mounted restore and combined initial-v1 acceptance remain explicit limits. Raw paper integration is separate work.
