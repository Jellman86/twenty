# Changing-universe panel

Implemented in `Twenty.Core.Research.ChangingUniversePanel` (`changing-universe-v1`). It is the bridge between the [v1 point-in-time dataset contract](data-contract-v1.md) and research returns over a universe whose membership changes: listings enter, leave, split and delist. The older [`MonthlyPanel`](../src/Twenty.Core/Research/MonthlyPanel.cs) requires every symbol present in every month, which is survivorship-biased by construction; this builder exists so future stock-level work does not inherit that bias. It computes returns only — it does not select holdings, size trades, feed the backtest or approve any strategy. All test inputs are authored **synthetic fixtures**, not market data.

## Construction

`ChangingUniversePanel.FromDataset(dataset, universeId, decisions, maximumPriceAge)`:

- `decisions` is an explicit, strictly increasing grid of 2–600 decision instants, none after the dataset snapshot cutoff.
- `maximumPriceAge` (at most 92 days) bounds how old the last session close may be at an endpoint. Older means unpriced, never silently carried forever.
- Eligibility at each decision comes from `DataSlice.Universe` on the slice as of that decision: only memberships and listings both *available* and *effective* then. Future knowledge cannot backfill an entrant or retrospectively remove a delisted name.
- Each month records eligible listings that have no fresh total-return-adjusted close as `UnpricedListingIds`, so a feature pipeline can see what it could not have priced at the time.

## Period returns

For consecutive decisions `t → t+1`, every listing eligible at either endpoint gets one classified entry:

| Status | Meaning | Return |
|---|---|---|
| `Priced` | Eligible at both ends, fresh adjusted closes | `close(t+1) / close(t) − 1` |
| `Entered` | Eligible at `t+1` only | none — no backfilled return |
| `TerminalZero` | Delisted with zero proceeds | `−1` |
| `TerminalCash` | Delisted with known cash per share | `cash / rawClose(t) − 1` |
| `ExitedUnexplained` | Left the universe with no known delisting | none — disclosed gap |
| `MissingPrice` | Eligible but no fresh close at an endpoint | none — disclosed gap |

Pricing choices are deliberate:

- **One adjustment vintage per period.** Both endpoint closes are read from the *later* slice. A split revises earlier adjusted bars; mixing the as-of-`t` January close with the post-split February close would fabricate a −50% month. These are realized outcomes for someone who held through the period, not decision features — features must still be built from as-of-`t` slices.
- **Terminal cash is priced from raw closes**, because cash proceeds are per actual share. Any split, dividend, merger or spinoff effective between the raw close and the later decision makes that ratio wrong, so the build fails (`universe-panel-terminal-complex`) rather than approximating. Mismatched cash/price currencies fail likewise; no FX is invented.
- **Missing terminal proceeds fail the whole build** (`universe-panel-terminal-missing`). Dropping a delisted holding is exactly the survivorship bias this type exists to prevent.
- **Knowledge lag is honoured.** A delisting effective before a decision but only *available* after it still explains the subsequent exit: the name remains eligible at the uninformed decision, carries its last close, and realizes the terminal return in the following period.
- A price-currency change for a continuing listing fails (`universe-panel-currency-change`); quote-unit (pence/major) changes are normalized by denomination.

`ReturnStatusCounts` and `HasGaps` summarize disclosure: any `ExitedUnexplained` or `MissingPrice` entry sets `HasGaps`. A simulator consuming this panel must refuse gaps or adopt an explicitly documented exit policy; averaging only `Priced` entries reintroduces the bias. The panel `Sha256` covers builder version, dataset/universe identity, staleness bound, memberships and every classified return, so a later registry can bind trials to the exact panel.

## Limits and next steps

- Returns are decimal ratios of adjusted closes; they inherit whatever adjustment quality the dataset asserts. Nothing here validates a vendor's adjustments against independent records (still open in [#5](https://github.com/Jellman86/twenty/issues/5)).
- Terminal-cash handling covers only the simple no-intervening-action case. Merger consideration in successor shares, spinoff distributions and partial cash/stock mixes are unimplemented and fail closed or classify as unexplained exits.
- Suspended listings leave `Universe` (it returns active listings only) and surface as unexplained exits unless a delisting action exists; suspension/resumption round-trips are not yet modelled as one holding period.
- No backtest, feature or execution code consumes this panel yet. Wiring it into a registered study needs a genuine point-in-time stock dataset, which does not exist yet; the French panels remain fixed-universe research-only inputs.
