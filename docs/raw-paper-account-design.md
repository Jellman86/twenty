# Raw paper-account integration: next contract

Status: unimplemented design boundary, following metadata plan preparation. This document does not count as initial-v1 paper acceptance or create an endpoint.

The current `PaperLedger` rehearses a frozen model allocation with authored GBP quotes and a buy-only planner. Raw studies instead use fractional shares, explicit executable FX, sales, ordered fee/cash constraints, action entitlements, unsettled claims and complete bound fill batches. Passing raw weights into the existing simulator would change those conventions. Viewing a stored report or advancing through its recorded marks is report inspection, not a paper account.

## Required first implementation

A raw account must be an independent persistent ledger, initially empty, with its own declared deposits and append-only chronological operations. Bind its originating study, selected candidate, selection artifact, plan, scientific identity and explicit execution assumptions once. Do not copy a development/final balance or treat an engineering screen as investment nomination. Existing model paper accounts and stored bytes must retain their current semantics.

The first bounded engineering account may use authored inputs only and must say so throughout the API/UI. That is useful operational evidence without claiming forward market validation. It still needs actual raw decisions, execution receipts, labelled FX, action accounting and cash reconciliation; it cannot substitute fresh GBP toy quotes for the supplied originals.

Each submitted operation needs a stable UUID and immutable request/input hashes. An exact retry returns the original recorded result; a changed payload under the same UUID refuses. Record a failed attempt without replacing its id or retrospectively applying a different outcome. Persist deposit, decision, ordered fills, action entitlements/payments and marks through the shared CPU reference. Publish the accepted state atomically with its operation journal; a process restart must not duplicate a deposit, fill, fee or claim settlement. Bound account/operation bytes and observation work, use the shared computation lease, and respect reserved artifact headroom.

Trade funding uses settled cash and permitted sale proceeds only. Expected receivables influence disclosed target proportions under the existing claim planner, and never create spending power. A pending stock delivery is not delivered inventory. Policy fees, reserves, shortfalls, cash floors and deferrals stay explicit. Reconcile actual fills and settlement against intentions rather than silently calling a completely filled batch after a cancellation or partial fill.

## Decisions that must be explicit before code

- Choose a chronological operation unit supported by the replay contract. `RawEventReplay` currently requires a complete rebalance batch and a final mark; an arbitrary prefix containing half a batch is not a valid replay archive. Either the first account admits complete offline batches and clearly declares that scope, or introduce a separately verified pending-order state machine before claiming asynchronous/partial-fill operation.
- Define the source-vintage contract for each operation. A later import must preserve prior observation identities/evidence and cannot revise a booked fill or grant earlier availability. Never load a study's final report or reserved market outcomes merely to open a paper account.
- Add paper information exposure to the shared reservation/disclosure boundary before permitting genuine sources. Authored-only operations are engineering fixtures; they do not establish the genuine guard. A new accounting or candidate variant must remain a recorded research change, not hidden search through paper resets.
- Define action-policy additions before ex-date/cutoff, separately from actual payment/FX evidence. Missing policy, late disclosure after quantity history, unsupported mixed consideration and successor history must refuse under precise retained codes.
- Specify inspection versus continuation across scientific builds. Historical bytes remain readable; changed code must not silently reinterpret or resume an account. An idempotent read/retry should not require opening market originals.

## Minimum TDD evidence

Start with a hand-calculated two-currency deposit/decision/fill/mark, then an authored dividend, split, positive cash exit and stock delivery. Pin exact net cash, quantities, fees and FX timing; include a pending claim across decisions without claim-funded orders. Include a cash-capped non-terminating decimal quantity, an unchanged portfolio, and delivered-plus-pending successor exposure.

For persistence, kill the owned native process at a commit boundary, reopen its private workspace and prove the same operation cannot deposit/pay/fill twice. Refuse changed UUID payloads, missing/corrupt originals, changed scientific identity, unsupported batch prefixes and conflicting journal state without silently repairing accounting. Check cancellation/partial-fill semantics against the declared first scope, shared computation contention, finite storage/work limits and preservation of existing model paper accounts. Exercise the API write guard and desktop/390px UI with no broker credentials or production experiment.

Only after these pass should the initial-v1 paper gate be reassessed. Genuine forward evidence, complete source coverage and live execution remain separate.
