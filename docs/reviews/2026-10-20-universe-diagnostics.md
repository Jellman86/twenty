# Changing-universe diagnostic checkpoint

Self-review and TDD record, 20 October 2026, under the owner's [initial v1 completion goal](../initial-v1-acceptance.md). This is an implementation review; no independent reviewer is claimed.

## Reproduced failures

Three authored .NET regressions failed before the adapter was added:

- A model diagnostic reported one trial when the same workspace contained a model and a universe trial.
- A completed universe anchor returned a failed diagnostic instead of its actual momentum/equal-weight/cash comparison.
- Tampering with its retained request returned `run-not-found`, because the universe register was never reached, rather than the expected `universe-request-integrity` refusal.

A separate built-asset browser regression reproduced `Late history response overwrote the latest selected diagnostic`: opening a slowly returned old model result followed by a universe result let the old response replace the user's newer selection. View revisions now guard inspection, analysis and polling updates; history refreshes have their own revision. The same browser case passes after the correction. Raw before/after browser evidence remains in private `artifacts/twenty/universe-diagnostics-20261020` storage.

## Implementation and downstream effects

- Both trial registers are captured under the shared research state/admission lock. Ordinary diagnostics disclose universe attempts; universe diagnostics disclose model attempts. Failed/incompatible searches stay visible. This improves disclosure, without claiming whole-programme statistical correction.
- Universe momentum paths use exactly their stored equal-weight/cash comparators, with no invented broad benchmark, FX conversion or learned prediction. The existing bootstrap/selection kernel is shared through a fixed two-comparator method. Ordinary four-comparator arithmetic and method labels remain unchanged.
- Scientific code identity, exact request bytes, dataset/universe/panel identities, decision grid, price-age policy, account assumptions and report paths are checked. Reports are loaded against the retained snapshot hashes. Checksummed but shortened or configuration-mismatched reports fail their contract.
- An input or computation failure preserves the journaled snapshot and cannot be silently retried under the same request id. Missing legacy requests are refused; retained evidence is not reconstructed or repaired.
- Optional identity/family metadata is omitted for old payloads, preserving legacy JSON shapes. Old artifacts and plans are not rewritten. Core/Research changes still change scientific identity, so an existing frozen study cannot spend a final pair under changed code without its existing guard refusing it.
- UI history labels follow the inspected report's actual family, including old model records opened from the universe section. The view states the lack of FX/external benchmark. Opening a history record is read-only; running a diagnostic is an explicit, journaled analysis of existing returns.
- Existing one-worker/deadline/candidate-count/journal capacity controls remain in force. No new training worker, capture request, genuine final evaluation, broker request or deployment setting is added by this adapter.

## Validation

558 .NET tests and 37 Python tests pass; eight new .NET cases use authored financial inputs. They cover family roles/disclosure/exclusions, independently computed mean active returns from the retained source reports, exact-request corruption, failed snapshot retention/replay, checksum-valid semantic corruption and missing legacy evidence/JSON compatibility. Existing bootstrap/accounting/study/reservation/restart/storage tests pass.

Ruff, Svelte check/build (zero warnings/errors), scientific-boundary checks and full-history/files secret scanning with its synthetic leak control pass locally. Built-asset shared-browser checks cover successful universe analysis, actual comparator labels, full registry disclosure, legacy-model history, delayed history selection and a 390-pixel layout, with zero production research writes and no page errors. T3's native preview lacked its host libraries; no administrative installation was made. No accelerator changes were made; these are engineering fixtures, not historical investment results.

## Remaining gates

V1-04 is still partial: registered changing-universe candidate selection/final evaluation remains open. Raw-price/FX accounting and terminal-data acceptance are unchanged open requirements. The five flagged post-last-trading rows and ACC's missing independent final-session evidence are not repaired or accepted by statistical analysis. Final-candidate runtime/persistence/UI acceptance follows passing CI/image publication and Git-backed Dockhand deployment; it is recorded separately after execution.
