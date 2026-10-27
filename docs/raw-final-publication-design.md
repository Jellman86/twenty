# Durable raw final publication — proposed contract

Proposed at `37c749c`; unimplemented. The [actual killed-final rehearsal](recovery-acceptance-2026-10-27.md) leaves a verified result orphaned before registry commit. This work changes publication durability, not the selected candidate, accounting or permission to calculate a final.

## Admission and capacity

New final claims opt into `raw-final-publication-v1`; historical parents omit the field and keep their old semantics. Before committing a new claim, reserve both final rows' maximum staging/report headroom in SQLite, in the same claim transaction. Refuse insufficient capacity before spending. Existing research artifact and dataset writers respect outstanding headroom. Each final keeps an original report plus its immutable staging envelope, so both copies count against the existing 256 MiB budget. Database/sidecar growth and foreign files remain operational limits; this is an application quota reservation, not a physical disk reservation.

Reservation rows remain bound to the exact study, original final UUID and trial ids, including after their remaining amount reaches zero. Missing/extra/mismatched rows fail closed. Success, scientific failure or explicit cancellation releases only unused headroom in the same family transaction. A pending publication retains its reservation. No claim/window/request/trial is released or replaced.

## Verified boundary

The worker calculates once and verifies all original receipts as today. It then records a `publishing` row with immutable publication metadata: stage/report hashes and lengths, original worker identity and verification time. This marker precedes file IO. A single bounded staging envelope contains a length-framed header plus the exact report bytes; an atomic flushed write avoids a separate manifest/body commit gap. The header binds original claim/study/trial/request/plan/dataset/scientific identity and report hash. Missing or corrupt staging cannot be rebuilt by running the candidate again.

Publication copies only those exact bytes into the existing report path, then atomically commits successful trial/parent/screen state and consumed headroom. A conflicting existing report is refused rather than overwritten. Publication errors after the verified marker remain `publishing`, with a retained error; they do not cancel the unstarted partner or rerun calculation. Calculation/verification failures before that boundary retain the current permanent-failure policy.

## Recovery and cancellation

At restart, enqueue each pending publication for one bounded worker-owned verification attempt; do not replay ledgers synchronously in the constructor. Recovery checks the persisted marker and exact envelope/header before loading any original dataset. It uses `RawStudyDevelopmentRun.Verify` on the same receipts under matching scientific code, and never calls `Run`/`RunStage`, changes a decision or creates a new trial. Publication can then complete and the original unstarted partner can run once. Other studies need not be blocked by a publication waiting for operator repair; the pending family's own partner waits.

An explicit same-origin recovery endpoint requires the original final UUID and plan hash. Repeating it after publication is a metadata replay. An incompatible scientific build, unreadable/missing/corrupt stage, unavailable exact original or publication IO fault leaves publication pending; recovery never guesses, regenerates bytes or accepts a replacement hash. Explicit stop remains terminal and prevents publication recovery; the claim stays spent and verified metadata/staging remain retained. Pre-marker legacy orphans are not adopted. The UI exposes publication status, clear errors and recovery only for a recorded original claim, without a new final-spend acknowledgement or any broker authority.

## Required TDD and acceptance

1. Insufficient pre-claim headroom: zero claims/new final rows/files, unchanged selection and exact development evidence. Other artifact/dataset writes cannot consume reserved headroom.
2. A deterministic SQLite failure at the success transaction leaves one verified staged result pending and its original partner planned. Removing that failure publishes identical bytes without calculation or new trials/claims, then the original partner runs once.
3. Missing/corrupt/swapped/coherently forged staged evidence refuses with the original marker retained. Exact restoration can recover; an existing conflicting final file is never overwritten.
4. Restart queues publication, preserves readiness and recovers only the original attempt. Cancellation, changed science and legacy orphans never reopen it. UUID aliases/replacements retain the final protocol's guards.
5. All claim/window, slot, request/report, margin and provenance invariants remain exact. Historical bytes/default omission/retries remain inspectable.
6. Fresh authored API and actual killed-process rehearsals, full suites, independent review where available, and Git/CI/image/Dockhand state-preserving deployment. No genuine holdout, vendor/broker path or production experiment is used.

Unavoidable limits: no recovery of bytes lost before a complete durable stage (except restoring exact original bytes from a private backup); no physical-disk guarantee; no automatic cross-build verification. Raw paper accounting remains separate work and will not be implemented by relabelling the older synthetic-quote simulator or a report viewer.
