# Registered universe study checkpoint

Self-review and test-driven implementation record, 21 October 2026 (Europe/London). This extends the deployed `ef9dee8` declaration foundation toward V1-04; no independent review of this new implementation is claimed. The [workflow contract](../universe-studies.md) describes the final behaviour.

## Regressions and downstream corrections

Three initial lifecycle cases failed against an explicit unimplemented stub: all trials/requests retained before computation, one frozen choice with one final pair, and cancellation without replacements. The implemented service now admits the complete family transactionally, uses the existing worker, validates exact requests/reports and retains all failures.

Implementation tests also exposed date-offset escaping changing immutable plan JSON during a state update. Plan values now use their original raw JSON bytes when the surrounding record changes. A controlled negative test holding the computation lease through idle backoff reproduced an idle worker refusing an ordinary universe request; the lease now releases before sleeping. Model/universe queues share computation capacity and retain admission order.

A further authored policy regression reproduced a 23-hour overlap becoming invisible when a spent universe final ended at 23:00 UTC and a later reserved window started at 00:00 on the same date. Exact timestamps now protect ordinary universe exposure, active study reservations and spent claims. The regression checks both refusal of the overlapping window and acceptance of a truly adjacent exact boundary. Legacy day-only windows remain conservative; original fields and artifacts are not relabelled. Stored study exposure must also agree with its retained request before work starts.

The shared budget case protects two final slots from both ordinary model and universe admission, then successfully allocates exactly the original final pair at the 1,000-trial ceiling. A separate full-metadata case exceeds the independent 1 MiB study-payload limit and is refused without committing a study/trial or writing new request files. These are different resource limits; a 1,000-trial ceiling does not promise every prior snapshot fits one study payload.

## Evidence, accounting and lifecycle effects

- Every declared candidate/base/stress pair is present before calculation. Prior model and universe attempts are captured together under admission locking; failed or legacy entries do not disappear. Positive margins do not certify a complete research programme's search history.
- Development uses a prefix that excludes the selection gap and final outcomes. Final requests use their separate warm-up grid and an empty account. The existing baseline rebalances fractional notionals monthly; it is not the contribution-only broker policy.
- Selection uses the worst base/stress notional margin over equal weight and cash. All four candidates must finish; a failed member stops the family. Paired diagnostics condition on the recorded return streams and their own statistic, not the wealth-margin selection rule or unknown upstream data.
- Exact dataset/configuration/cost roles, report grids, contributions and summary values are checked. Corrupt request bytes, rehashed semantic report changes, narrowed exposure metadata, input loss and code drift fail closed. No failed final can cause a runner-up substitution.
- Final request identity, spent window and both final IDs are atomic before computation. Replays survive input loss/restart; different UUIDs cannot replace a spent pair. Cancellation retains its claim. Planned work can resume under unchanged code; interrupted work stops its family.
- Scientific identity includes the changed Research service/worker policy. Host admission and worker commits remain distinct provenance. Completed legacy plans/reports stay readable; new optional universe metadata is omitted when absent. Exact old plan bytes are not reconstructed from evolved DTOs.
- Additive tables preserve model-era rows. A compatible complete recovery is required; an older writer cannot enforce the new universe-study reservations. No backward writer migration is claimed.
- The API and UI clearly retain local-currency/GBP-notional limitations. Imported-data success is a research screen, not an investment/forward/broker nomination. Raw execution, GBP FX and unsupported terminal events are not solved by selection.

## Validation

608 .NET tests and 39 Python tests pass locally. The new lifecycle cases cover pre-work admission/replay, canonical offsets, malformed grids, complete selection/final spending, final failure/cancellation/input loss, restart, code changes, both reservation directions, prior exposure, exact time boundaries, semantic report corruption, exact request/exposure checks, independent payload/trial limits and final-diagnostic exclusion. Authored fixtures with false synthetic flags test policy guards only; they are not historical data. No accelerator changes were made.

The local HTTP rehearsal imports a hash-verified authored dataset, registers all eight development trials, independently recomputes all four margins using Python decimals, verifies the frozen choice, rejects a wrong-plan final request, records/replays exactly two final trials and refuses replacements. Each final comparator has 24 contributions totalling 480 notional units. Exact plan/request/report responses survive restarting the local process. A final report cannot become a development diagnostic anchor. This starts at authored validated observations; capture-to-price integration is a separate acceptance requirement.

A built-asset shared-browser replay uses those authored server responses. It verifies stable declaration/final UUIDs after simulated lost responses, late history selection protection, development/final report inspection, final-diagnostic hiding, immutable plan links, correct synthetic labels and a 390-pixel layout. No page errors or production research/execution writes occur. A visual check prompted consistent form controls and shorter explicit-UTC labels. Native T3 preview opening remains unavailable because host browser libraries are missing; no administrative installation was performed.

Ruff, Svelte check/build (zero warnings/errors), scientific-boundary checks and full-history/files secret scanning with its deliberate fake-secret refusal are part of the checkpoint gates. Private full API/browser records are under `artifacts/twenty/universe-study-20261021`; earlier negative controls remain under the preceding session's private artifacts. No vendor request, broker request, production trial or genuine final holdout was used by acceptance. CI/image/deployment receipts are recorded after execution.

## Remaining initial-v1 gates

This completes the changing-universe study implementation, subject to its published-image/deployment acceptance. It does not close raw-price/GBP-FX accounting, terminal-data acceptance, the combined capture-to-study workflow or all recovery/security cases. The source-bound six-symbol audit still refuses its documented unsupported evidence; no row or source assertion was altered to make the study succeed. No release or live execution is introduced.
