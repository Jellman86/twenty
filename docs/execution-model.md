# Executable prices and sizing (`execution-v1`)

**Status: planner and raw settlement run implemented 22 October 2026 (`ExecutionPlanner`, `FillPolicy`, `RawExecutionRun`); backtest mode exposure and report disclosure are not.** Written to settle the approach for the last named gap in the [roadmap](roadmap.md)'s P0 changing-universe row — "executable-price handling" — and the sizing/execution contract that the [raw GBP ledger](raw-gbp-ledger.md) explicitly defers. Nothing here is a claim that any order would have filled.

## The problem

Two separate questions are currently conflated into one flat cost rate.

**What price could a trade have achieved?** `UniverseBacktest` fills at the decision-time close, and `RawGbpLedger.Fill` takes its price on trust. Both are wrong in the same way: the decision is made *after* the session closes, using that close, and the fill is then priced at that same close. That is only achievable in the closing auction, submitted before the decision was made. It is look-ahead, and the flat `HalfSpreadBps`/`SlippageBps` charge does not repair it — it prices the trade at information the trader had not yet acted on. The adjusted basis compounds the problem: `TotalReturnAdjusted` is a research series, not a quote anyone transacted at.

**What size could a trade have been?** Notionals are fractional GBP with a minimum order and no share step, no volume participation and no rejection. `RawGbpLedger` records a supplied quantity and fee and says plainly that "quantity steps, minimum orders, spread, slippage and cost reserves belong to a later sizing/execution contract".

## Proposed convention

**Decide on the close, fill at the next session's open.**

- A decision taken at the end of session *t* may use everything through *t*'s close, as today.
- Its orders fill at session *t+1*'s **open**, on the **`Raw` basis** — execution is a share transaction, not a return index — converted with an explicit `Executable`-purpose FX observation when the listing is not GBP.
- If no *t+1* session exists before the next decision — halt, suspension, delisting, or simply never captured — **every order that would have filled there is rejected** with a recorded reason. Nothing fills at the close that was just observed.

The check is mechanical: a fill price must equal a bar open that became available *after* the decision that produced it. A test can therefore assert that filling at the decision close is impossible.

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

`BuyTaxBps`, `FxBps` and `MinimumOrderGbp` stay as separately reported charges. Every receipt records the modelled fill **and** the observed open it derives from, so the distance between them is auditable rather than buried in a rate.

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
3. `UniverseBacktest` exposes the raw run beside the research-notional mode rather than only alongside it.
4. Report disclosure: per-trade fill versus observed open, and rejection counts by reason.

## Decisions recorded (22 October 2026)

1. **Fill convention: strictly the next session's open.** A close-auction fill was rejected because it needs intraday timing the dataset does not carry.
2. **Quantity step: fractional.** A fractional fill is a broker product feature, but the research portfolio already sizes fractionally; the step remains available.
3. **Participation cap: 5% of the fill session's share volume.**
4. **Keep the notional mode**, relabelled research-notional — it is the only mode a genuine panel can run today.
