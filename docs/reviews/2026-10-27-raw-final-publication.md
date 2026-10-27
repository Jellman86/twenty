# Raw final publication validation and review boundary

Source `2932574` implements the [opt-in publication contract](../raw-final-publication.md), following the earlier Medium publication-loss finding and the actual killed-process evidence at `81f88b5`. The original Big Pickle recommendation was staged, original-attempt-only recovery with pre-claim headroom and no new candidate work. The new design review request at `b7d7836` failed at the provider's free usage limit before inspection. There is no independent review verdict for this implementation; the checks below are parent validation.

## Red cases and changes

- With only approximately 1 MiB unallocated, old admission spent a claim rather than reserving both final results. New admission refuses before claim, pair or request writes.
- A deterministic SQLite fault at the success transaction used to fail the verified row and cancel its partner. A `publishing` marker, immutable framed stage and original-byte publication now retain that attempt and its waiting partner.
- Coherently changed report/receipt trial bindings reached the dataset resolver before later verification rejected them. The test removes that dataset and distinguishes this read from a preflight refusal. New scope preflight rejects both before any original is read.
- A blocked family's planned partner starved an unrelated universe study. Raw and universe scheduling now share the runnable-family filter, excluding that waiting partner.
- A coherently rewritten final-v1 report could be read under a new publication claim. New protocol reads require its current final-v2 report era; actual historical final-v1 records stay readable.
- Repeating the same failed recovery grew the audit journal. Identical pending errors now preserve the same journal state while allowing exact repair and retry.

The initial fault trigger also affected already successful development rows; its corrected predicate targets only transitions into success. A first corruption matrix assumed the wrong archived-dataset path and failed in the harness. These failures are retained privately and are not claimed as product regressions.

## Acceptance at the source checkpoint

Local full suite: 1,374 .NET tests, 45 Python tests, Ruff, Svelte check/build, scientific boundary and secret checks pass. Four fresh compatibility clones under `raw-publication-compat-{1..4}-20261027` preserve actual older final-v1, frozen v3, raw-v1 and universe report/request/selection bytes and UUIDs. New first-final spending under changed science still refuses.

`raw-publication-api-20261027` starts from a verified publication transaction fault, refuses a corrupt stage, preserves the original marker, enforces the write guard and original UUID, then publishes exact restored bytes and completes the original partner. All eight development reports, ten requests and selection remain exact; the one synthetic claim remains, with no genuine claim. Cold restart preserves original metadata/report replies. Readiness was approximately 1.2 seconds.

`raw-publication-kill-20261027` kills an actual native process after both stage/report writes while a test-only SQLite writer reservation prevents registry commit. Startup recovers its unchanged marker, worker/start time and verified bytes, finishes the original pair and releases unused headroom. It refuses a replacement final; completed metadata/report replay succeeds with dataset originals unavailable. It retains ten original trials, one synthetic claim, zero genuine claims, exact requests/development reports/selection and no broker or production write. This supersedes only the post-durable-stage loss case in the earlier crash rehearsal.

Authored intercepted browser evidence at `raw-publication-ui-final-20261027` covers original-claim recovery/error/retry, verification hashes at 390 pixels, no new spend acknowledgement, historical-build controls, all prior lifecycle controls and delayed report selection. No page errors or production writes occur. The native T3 preview is unavailable because browser libraries are missing; the shared authenticated fallback uses an isolated context.

## Limits and downstream effects

Recovery uses the original claim, trial and immutable hashes. Verification reconstructs frozen requests and replays ledgers under matching scientific code/runtime; it is not merely a bookkeeping read. It owns the shared computation slot and a two-minute bound. Cancellation remains terminal. Missing original stages, cross-build verification, physical disk/foreign database growth and fully coherent operator replacement of the datastore are outside its guarantees. Duplicated staging consumes storage. New `publishing` records require a compatible reader; do not downgrade an active new-protocol store to a binary that cannot read that state.

Core accounting/strategy code is unchanged, while Research assembly identity changes: historical completed evidence remains inspectable; old unspent declarations do not silently continue. Legacy orphan files are not adopted. No source/vendor/broker call, genuine final evaluation, production experiment, paper promotion, release or profitability conclusion is part of these checks. CI/image/Dockhand acceptance is recorded separately after completion.
