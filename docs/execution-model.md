# Executable prices and sizing (`execution-v3`)

**Status: offline planner and settlement reference with bound sessions and pinned execution-time FX (`execution-v3`, `raw-execution-v3`).** The [current timing contract](execution-timing.md) requires the exact next session from the reviewed calendar and the exact foreign FX observation available by its open. Registered raw mode, terminal/action scheduling and genuine execution acceptance remain open. Earlier v1/v2 checkpoints below are historical context, not the current timing contract.

## The problem

Two separate questions are currently conflated into one flat cost rate.

**What price could a trade have achieved?** The notional backtest uses decision-boundary return-index accounting. That does not establish executable closing-auction trades. `RawGbpLedger.Fill` deliberately accepts a supplied raw fill assumption; it does not itself infer or certify execution. A strategy using a session close needs a later execution convention. `TotalReturnAdjusted` is a research series, not a transacted quote, and a flat cost rate cannot establish execution timing.

**What size could a trade have been?** Notionals are fractional GBP with a minimum order and no share step, no volume participation and no rejection. `RawGbpLedger` records a supplied quantity and fee and says plainly that "quantity steps, minimum orders, spread, slippage and cost reserves belong to a later sizing/execution contract".

## Proposed convention

**Decide on the close, fill at the next session's open.**

- A decision taken at the end of session *t* may use everything through *t*'s close, as today.
- Its orders fill at session *t+1*'s **open**, on the **`Raw` basis** — execution is a share transaction, not a return index — converted with an explicit `Executable`-purpose FX observation when the listing is not GBP.
- If no *t+1* session exists before the next decision — halt, suspension, delisting, or simply never captured — **every order that would have filled there is rejected** with a recorded reason. Nothing fills at the close that was just observed.

The v3 implementation requires the exact next session from the source-bound calendar; missing raw bars are refused rather than replaced by later observations. Foreign FX is explicitly pinned, fresh and available by the modelled open. Neither a daily bar nor an executable-purpose rate certifies an actual opening-auction fill. See the [current contract and limits](execution-timing.md).

## Sizing

| Rule | Proposed v1 | Why |
|---|---|---|
| Quantity step | **Fractional** (`QuantityStep` 0, the default; a positive step snaps down) | Agreed 22 October: a fractional fill is a broker product feature, and the research portfolio already sizes fractionally. The step is available for a caller who wants whole shares. |
| Minimum order | In the **listing's own major currency**, default 1 | The planner is FX-free by design. The GBP floor stays in `ResearchCosts.MinimumOrderGbp` for the notional mode. |
| Volume participation | At most 5% of the fill session's share volume | The bar carries raw `Volume`; a £20/month research portfolio is far under it, but the constraint must exist before larger notionals are used. |
| Unfunded orders | Refused by the ledger (`raw-ledger-cash`), not here | Funding is account state; the planner decides only what market data can support. |
| Partial fills | Not modelled | A v1 order either fills whole or is refused. |

## Fill price and costs

Spread and slippage move onto the **price**, so the executed price is explicit and comparable with the observed open:

```
buyFill  = open × (1 + halfSpreadBps + slippageBps)
sellFill = open × (1 − halfSpreadBps − slippageBps)
```

Those notional-engine cost fields are not imported into this offline run. It charges the caller's explicit GBP fee per booked fill; security taxes, FX spread beyond the supplied rate and a GBP minimum remain outside this reference. Every receipt records the modelled fill **and** the observed open it derives from, so the distance between them is auditable rather than buried in a rate.

## Refusals, never approximations

| Reason | Meaning |
|---|---|
| `no-next-session` | No session after the decision before the next decision. |
| `no-open` | The next bar carries no usable open. |
| `open-not-observable` | The open is not available before the next decision (point-in-time). |
| `below-minimum` | Order value under the minimum. |
| `volume-participation` | Order exceeds the participation cap. |
| `unsupported-action` | A split, merger, spinoff or delisting is effective between decision and fill; the ledger already refuses these. |
| `invalid-order` | Not a trade: no listing, no positive notional, or a next decision that does not follow the decision. |

Funding is deliberately **not** a planner refusal. It is account state, and `RawGbpLedger` already refuses an unfunded fill atomically (`raw-ledger-cash`), leaving cash, holdings and fees unchanged. The split keeps the planner a pure function of market data and the ledger the sole authority on what can be paid for.

A rejected order leaves cash, holdings and fees unchanged — the ledger's existing atomicity rule.

## Explicitly out of scope

Order books and quotes, partial fills, intraday order timing, security-level taxes and fees, borrow and shorting, settlement cycles, broker rejection reasons, and any statement that a real order would have executed. A `ConservativePolicy` availability convention (next session open) is used for the fill bar; it is not evidence of the vendor's publication vintage.

## Deliverables

1. **Done.** `FillPolicy` and `ExecutionPlanner.Plan` in `Twenty.Core.Research`: planned orders become `Fill | Rejected(reason)`, with the modelled fill price, the observed open it derives from, and the bar id that evidences it.
2. **Done.** `RawExecutionRun` settles a sequence of decision points through the planner into a `RawGbpLedger`, so the price and quantity the ledger takes on trust are produced by this contract instead. Valuation is raw closes with the caller's explicit FX purpose, taken **before** each month's fills because those fills happen after the decision. It is deliberately separate from the notional engine, which is never relabelled as executable.
3. `UniverseBacktest` exposing the raw run beside the research-notional mode. **Blocked, deliberately not attempted.** `RawGbpLedger.CheckHoldings` refuses any unbooked, non-neutral action effective after a position opened (`raw-ledger-action-unsupported`), and a delisting is exactly such an action. A changing universe's whole purpose is that names leave, so a raw run over one would throw on the first terminal exit rather than settle it. The ledger now has [explicit terminal retirement/settlement primitives](raw-terminal-accounting.md), but the raw run still lacks event scheduling and pending-claim valuation. Simply adding the planner to the notional loop would still omit those states. Until then the raw run covers continuing positions only and refuses effective unsupported actions.
4. Report disclosure: per-trade fill versus observed open, and rejection counts by reason.

## Decisions recorded (22 October 2026)

1. **Fill convention: strictly the next session's open.** A close-auction fill was rejected because it needs intraday timing the dataset does not carry.
2. **Quantity step: fractional.** A fractional fill is a broker product feature, but the research portfolio already sizes fractionally; the step remains available.
3. **Participation cap: 5% of the fill session's share volume.**
4. **Keep the notional mode**, relabelled research-notional — it is the only mode a genuine panel can run today.


## Corrected v2 accounting and timing contract (22 October 2026)

Review of `4514901` reproduced three defects despite its passing suite: sell quantities were booked positive, major-currency notionals were divided by pence quotes, and closing valuation went backwards to the final decision after booking its later fills. The v2 reference fixes all three. Quantity sizing and the post-step minimum use `Denomination.ToMajor(fillPrice)`; reported fill prices remain in original quote units. Ledger quantities are signed by buy/sell direction. Decision rows remain pre-fill marks; `ClosingValuation` is a distinct post-fill mark at the final `FillMarket.DecisionAt`, with its cash, positions, purpose and time retained. `Entries` exposes immutable signed ledger receipts; `ClosingEquityGbp` equals that closing mark's equity. No trailing empty step is required.

Decisions must increase strictly, and each prior fill/booking slice must be no later than the following decision. Orders must use their own step's decision time and may not extend beyond the following decision. Invalid chronology is refused, not repaired by reordering. Callbacks run in supplied order and can observe only the supplied valuation argument through this API; the offline callback is not a sandbox or a registered strategy/policy artifact.

Planner rejections return recorded reasons without ledger changes. Insufficient cash (including fees), overselling, missing executable FX and other ledger/evidence violations abort the run; no completed partial result is returned. Orders are booked in caller order, with no automatic sell-first reordering or financing. The local ledger remains inaccessible after a thrown run. This differs from a durable per-order rejection journal, which remains future integration work.

In v2, the fill slice was an explicit **booking/knowledge** snapshot. This paragraph describes the former FX assumption, superseded by [v3](execution-timing.md). Its executable FX is not evidence of a rate available at the earlier bar's open. Receipts retain that booking time and rate; a reference-purpose closing mark may use a different rate and cannot fund the fill. Source-bound execution-time FX, complete session coverage, action/payment scheduling, terminal settlement and registered replay remain open. These limitations appear in the returned result; none of this checkpoint's authored examples establishes executable historical returns.


## Publication and deployment acceptance (22 October 2026)

Source `022249ccfcc5c3f3a9547fc782502c282a8ebf6f` passed [CI 39822594489](https://github.com/Jellman86/twenty/actions/runs/39822594489), including 793 .NET tests, Python/web checks, boundary/security guards, image smoke and registry publication. Four exported authored runs match independent Python Decimal accounting; proof is retained under `/mnt/apps/developer/jellman86/artifacts/twenty/execution-v2-20261022`.

A fresh compatibility clone under the changed scientific identity preserved a completed authored universe study's exact result/plan and ten request/report pairs. Its original final request replayed the same pair, and a replacement remained refused with ten trials unchanged. This used the private clone only, with no production trial or genuine holdout. Proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/universe-study-cross-version-execution-v2-20261022`.

After confirming there were no active production runs or studies, the commit-specific image digest was verified against `dev` and deployed by Git-backed Dockhand sync/deploy. Live metadata reports the exact source commit and one running healthy container. Eight stored collections, 58 byte-identical reports, four diagnostic responses and completed study plans were preserved; the production register remains 61 model trials and zero universe trials, with seven paper accounts unchanged. Broker connection and automatic purchases remain disabled.

Read-only shared Playwright checks passed at desktop and 390px widths without page errors, overflow or research/execution writes. Native preview was attempted first and reported missing host browser libraries. Deployment receipts are under `/mnt/apps/developer/jellman86/artifacts/twenty/deploy-execution-v2-20261022`. This checkpoint does not complete the registered raw execution engine or initial-v1 acceptance.

The next terminal-settlement contract must retain entitlement, actual settlement/payment and booking/availability separately, with explicit cash currency/FX or successor share terms and immutable source bindings. Panel return statuses alone do not establish when proceeds become spendable or provide an executable conversion. Copying the panel's period-end ratios directly into this quantity ledger would lose those distinctions; the current fail-closed boundary remains until that contract and its tests exist.
