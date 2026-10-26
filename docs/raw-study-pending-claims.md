# Registered monthly pending claims

The current `raw-study-development-v4` / `raw-study-final-v2` calculation can carry a declared dividend or pure cash/stock terminal claim across monthly marks and decisions. This extends the settled-action [distribution contract](raw-study-distributions.md). Claims remain separate from delivered shares, cash and actual fees. Every claim must settle by the stage's last mark; a pending final mark refuses the trial and cannot contribute to selection or the final screen.

## Frozen expectations and admission

A `studyDividend` or `studyTerminal` catalog row can carry a separate `markPolicy`: currency, expected withholding **per cutoff share**, expected fixed GBP fee **per entitled account**, availability timestamp, source reference, content hash and allocation rule `raw-study-claim-mark-per-share-fixed-gbp-fee-v1`. Withholding cannot exceed the action's cash per share. Stock policies have null currency and zero withholding; confirmed zero outcomes refuse a policy. These are supplied expectations, not authenticated tax or broker promises. They may legitimately predate action disclosure, but must be available at the mark that uses them.

A single conversion multiplies expected withholding by the ledger's frozen entitled quantity. The fixed fee is not scaled. No-holding dispositions produce neither a receivable nor a fee. Later delivery withholding, fees and payment FX remain separate actual inputs; no earlier estimate is inferred from them.

Admission checks the declared timing metadata for both stages. Any catalogued claim whose capture and delivery straddle a monthly mark must carry a policy available by the first such mark (`raw-study-claim-policy-coverage`), before trials or final claims exist. Synchronous deliveries before a mark can retain their original policy-free catalog bytes. This preflight does not read reserved prices, prove source-wide action coverage or guarantee a future attempt will succeed. Missing action originals, stage-end unsettled claims, unsupported successor history, insufficient actual settlement cash, quotas and other calculation/publication failures still retain the attempted trial and permanent final claim under the [final failure policy](raw-study-final.md).

## Expected proportions, settled funding

`raw-rebalance-v1` retains its original settled-only calculation and unpaid/unsettled refusal. `PlanWithReceivables` produces `raw-rebalance-claims-v1`, carrying declared total-amount claim assumptions. It recomputes their valuation and derives targets from expected total equity after the user reserve and conservative fill-fee budget. An unchanged surviving holding is therefore not sold merely because another holding has become an illiquid claim. Gross pending successor value is credited to that successor's target before calculating desired delivered shares.

Orders use only delivered holdings and settled cash. Ordinal sales precede ordinal cash-capped purchases. Positive cash receivables and undelivered shares never fund an order. Expected negative cash-claim values and stock-delivery fees impose an additional cash floor; a positive claim cannot cancel another claim's cash fee requirement. These floors do not subtract the fee from expected equity twice.

Each target distinguishes desired quantity from `fundedTargetQuantity`; plans disclose `deferredOrderValueGbp`, `claimCashReserveGbp` and `reserveShortfallGbp`. Temporary illiquidity can leave an unmet reserve or deferred purchase, with no borrowing. Subsequent market gaps, quantity steps, minimums, participation and actual fees still apply to every emitted order: fills remain an exact ordered batch or the trial refuses. This is a declared allocation model, not a broker funding guarantee.

## Marks, returns and replay

Every contribution is followed by an explicit `mark-open-i`, before the decision. `openingEquityAfterDepositGbp` is that mark's **expected equity**, with the deposit counted once. Monthly closing equity uses the same basis. A payment replaces the receivable with actual cash/shares; any FX or deduction discrepancy appears in that period exactly once. A successful last mark has no pending claims, so expected and settled equity agree at the screen. Nonpositive opening expected equity refuses.

Monthly summaries disclose pending claim count, net expected claim value and expected claim fees. Summary `feesGbp` continues to mean actual booked fees; estimates are never added to it. Full receipts retain holdings/cash and claim components at every opening and closing mark.

Replay dispatches explicitly on the plan version, independently revalues declared assumptions and recomputes target quantities, cash floors, deferrals and orders. Registered replay additionally reconstructs every assumption and opening/closing mark from the original frozen catalog. An internally coherent offline assumption change cannot replace a registered policy. The new feature permits at most eight pending claims per account at a mark/decision; the existing byte, event, slice and observation-work limits still apply. Repeated cached marks and each claim's work are charged, so the eight-claim bound does not promise every large input fits the archive.

Stock claims retain the original strict successor checks: a current active listing, fresh raw price/FX and no intervening non-neutral successor action are required. Pre-listing successors, mixed consideration, distributions before delivery with unsupported history, partial delivery and cash in lieu are explicitly unsupported. No successor listing or quote is invented.

## Compatibility and evidence

New nullable/default fields are omitted when absent, preserving settled-only plan/catalog JSON. Historical final-v1 and current final-v2 reports remain inspectable with exact hash/identity checks; original completed final UUIDs still replay as metadata before checking current code. A changed Core/scientific build makes old unspent studies read-only: starting, selecting or first-spending them is refused. Frozen selection/report bytes remain original. Current verification reconstructs current v4/v2 calculations; cross-build scientific reexecution is not promised.

Authored regressions cover £1.10 / £0.50 / £0 pending income from one shared policy, cash/stock settlement across a decision in each stage, exact opening identities, separate future actual deductions, no survivor liquidation, fully illiquid reserve shortfall, fee cash floors, per-claim work/capacity, decimal underflow and registered input/plan tampering. These are constructed accounting fixtures, not historical results or genuine holdout evidence. Independent review and private API acceptance are recorded separately when completed.
