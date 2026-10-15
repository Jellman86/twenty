# Astra review: current signals and broker coordinator

15 October 2026. Independent GPT-6 Astra review of the new uncommitted signal/coordinator boundary. The review used temporary copies and authored offline fault injection; it made no repository/infrastructure changes or authenticated broker calls.

## Design findings

The official schema does not establish wallet net-value signs or tax inclusion. The implementation therefore requires a privately evidenced explicit profile, archives exact bodies, distinguishes observed quantities from booked debits, and does not infer cash from pending quantities or absolute values. Unknown submissions require deliberate attribution to an independently inspected broker order ID; no automatic price/time match or POST replay is used.

The reviewer also required funding uniqueness independent of model versions, a total held/pending-name cap, positive terminal fill evidence, actual debit-overrun recording, per-submit revalidation and demo receipts covering coordinator/transport code. Those contracts are implemented and exercised with authored tests; none constitutes authenticated broker acceptance.

## Code findings and fixes

- **P1: broker state stale within a cycle.** An external pending order after the first submission did not prevent the next. Fixed by refreshing positions/pending/history and reconciling known fills before each POST, followed by a fresh account/funds check, policy/quote reload, exposure and external-order checks. Permanent regression injects an external order after the first POST and verifies no second submission.
- **P2: changed rules bypassed demo evidence.** Refreshed quote/rule metadata was not checked against the batch and demo receipt. Fixed by comparing the current immutable contract/rule hashes to both immediately before submitting. Regression changes a share step after the initial live gate and verifies the plan remains unsubmitted.
- **P2 follow-up: a core fill-reconciliation error was not persistent.** Contradictory REJECTED/positive-fill history blocked one cycle but could disappear from a subsequent scan. Broker reconciliation now normalizes that error to a persistent broker integrity code and also classifies the original core code defensively. A regression preserves the block after history/positions lose the contradictory row, with two remaining planned orders never submitted.

The narrow follow-up independently confirmed the first two fixes and that fill-revision blocks survive restart/truncated history. Existing tests were updated for intentional per-order reconciliation (earlier fills can now be booked during the same cycle). A final narrow review verified the integrity-classification fix: 28 execution tests and three independent tests passed, covering disappearing contradictions, later valid history, block clearing and restart persistence. No new material finding was reported in that scope. The complete local suite subsequently passed 214 .NET tests, including semantic decimal/timestamp deduplication. Final runtime/CI checks are recorded with the implementation checkpoint.
