# Changing-universe panel

Implemented in `Twenty.Core.Research.ChangingUniversePanel` (`changing-universe-v1`). It is the bridge between the [v1 point-in-time dataset contract](data-contract-v1.md) and research returns over a universe whose membership changes: listings enter, leave, split and delist. The older [`MonthlyPanel`](../src/Twenty.Core/Research/MonthlyPanel.cs) requires every symbol present in every month, which is survivorship-biased by construction; this builder exists so future stock-level work does not inherit that bias. It computes returns only — it does not select holdings, size trades, feed the backtest or approve any strategy. All test inputs are authored **synthetic fixtures**, not market data.

## Construction

`ChangingUniversePanel.FromDataset(dataset, universeId, decisions, maximumPriceAge)`:

- `decisions` is an explicit, strictly increasing grid of 2–600 decision instants, none after the dataset snapshot cutoff. The grid must be **monthly**: consecutive decisions fall in consecutive calendar months (evaluated on their UTC instants) and span 20–45 days. Hourly, weekly, twice-monthly or quarterly grids are refused (`universe-panel-decisions`) because the consuming backtest deposits, measures momentum and annualises in monthly units, and a denser or sparser grid would be silently misinterpreted as months.
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
| `TerminalStock` | Delisted into successor shares at an exchange ratio | `ratio × successorRawClose(exit) / rawClose(t) − 1` |
| `ExitedUnexplained` | Left the universe with no known delisting | none — disclosed gap |
| `MissingPrice` | Eligible but no fresh close at an endpoint | none — disclosed gap |

Pricing choices are deliberate:

- **One adjustment vintage per period.** Both endpoint closes are read from the *later* slice. A split revises earlier adjusted bars; mixing the as-of-`t` January close with the post-split February close would fabricate a −50% month. These are realized outcomes for someone who held through the period, not decision features — features must still be built from as-of-`t` slices.
- **Terminal cash is priced from raw closes**, because cash proceeds are per actual share. Any split, dividend, merger or spinoff effective between the raw close and the later decision makes that ratio wrong, so the build fails (`universe-panel-terminal-complex`) rather than approximating. Mismatched cash/price currencies fail likewise; no FX is invented.
- **Missing terminal proceeds fail the whole build** (`universe-panel-terminal-missing`). Dropping a delisted holding is exactly the survivorship bias this type exists to prevent.
- **Knowledge lag is honoured.** A delisting effective before a decision but only *available* after it still explains the subsequent exit: the name remains eligible at the uninformed decision, carries its last close, and realizes the terminal return in the following period.
- A price-currency change for a continuing listing fails (`universe-panel-currency-change`); quote-unit (pence/major) changes are normalized by denomination.

`ReturnStatusCounts` and `HasGaps` summarize disclosure: any `ExitedUnexplained` or `MissingPrice` entry sets `HasGaps`. A simulator consuming this panel must refuse gaps or adopt an explicitly documented exit policy; averaging only `Priced` entries reintroduces the bias. The panel `Sha256` covers builder version, dataset/universe identity, staleness bound, memberships and every classified return, so a later registry can bind trials to the exact panel.

## Backtest over the panel (`UniverseBacktest`, added later the same day)

`UniverseBacktest.Run` (`changing-universe-backtest-v1`) steps the existing decimal `NotionalPortfolio` through the panel with three non-learned baselines: equal-weight over every holdable listing, 12-1 momentum (compounded `Priced` period returns over 11 periods skipping the most recent; fixed constants, not searched), and cash. At each decision the holdable set is the month's eligible listings **minus** its `UnpricedListingIds`: a name whose price was not observable at that decision cannot be bought there, however briefly stale its price was. (A previously held name that is unpriced at the *next* decision would have already produced a `MissingPrice` gap in that period, which the backtest refuses, so the exclusion cannot silently drop a held holding.) The backtest **refuses gap panels** (`universe-backtest-gaps`): a portfolio cannot be stepped honestly through a month in which a possible holding has no return. Momentum candidates need a complete priced trailing window, so entrants are excluded until one exists — a disclosed selection rule. The report binds the panel `Sha256`, dataset and universe ids and synthetic flag, and states its limits in-band: local-currency returns with no FX conversion (GBP notionals are an accounting unit, not achievable GBP wealth), simple terminal proceeds only, fractional full-rebalance accounting. Tests hand-verify the first equal-weight period, the −23.5% four-name terminal month, the entrant's first momentum eligibility, gap/short-history refusal and cost drag. This is an engineering baseline over synthetic fixtures; it is not connected to the experiment registry and makes no market claim.

## Offline CLI

```sh
dotnet run --project src/Twenty.Data -c Release -- universe-backtest --dataset <input.json> --universe <id> --decisions <iso8601,iso8601,...> [--max-price-age-days 45 [--top-k 3]]
```

Decisions must carry explicit UTC offsets. The command prints one JSON object: panel identity/hash, month/period counts, per-status return counts and `hasGaps`, plus backtest summaries (default £20/month and default research costs) when the backtest runs. A refused backtest (gaps, insufficient history, invalid config) still prints the panel block with a `refusal` code and exits 1; dataset-selection failures exit 1 with the structured issue code. Smoke-verified against an independently generated JSON copy of the test scenario: 17 evaluated periods, £340 contributions, cash exact, equal-weight maximum drawdown −23.5% at the terminal month.

## Registered trials

Universe backtests can now be *registered trials* rather than ad-hoc CLI output. `ResearchStore` (partial `UniverseRuns`) keeps a separate `universe_runs` register in the same SQLite database: v1 datasets are imported content-addressed (`universe-datasets/<sha256>.json`, via the `imports/` inbox over HTTP), and `ExecuteUniverseRun` inserts the attempt as `started` **before** computing, then records exactly one terminal state — `succeeded` with panel and report hashes (report stored immutably at `runs/<id>/universe-report.json`), or `failed` with the refusal code. Gap-panel and missing-terminal attempts therefore count as trials. Replaying a request id returns the original entry whatever its status; changing the semantics under the same id is `request-id-conflict`; a process death mid-trial leaves `started` rows that store opening marks `interrupted`. HTTP surface: `POST /api/research/universe-runs/import-dataset`, `POST|GET /api/research/universe-runs`, `GET /api/research/universe-runs/{id}[/report]`. Execution is synchronous in v1 (the store method runs the backtest before returning), bounded by the 8 MiB dataset cap and 600-decision limit. This register is deliberately separate from the frozen study machinery: no promotion, holdout or diagnostic consumes universe runs yet, and there is no UI. The offline CLI above remains unregistered — its output is exploratory and must not be quoted as trial evidence.

## Limits and next steps

- Returns are decimal ratios of adjusted closes; they inherit whatever adjustment quality the dataset asserts. Nothing here validates a vendor's adjustments against independent records (still open in [#5](https://github.com/Jellman86/twenty/issues/5)).
- Terminal-cash handling covers only the simple no-intervening-action case; `TerminalStock` now covers a simple stock merger (successor raw close at exit times the exchange ratio, same no-intervening-action guard and currency checks), and spinoffs, partial cash/stock mixes and suspensions remain unimplemented and fail closed or classify as unexplained exits.
- Suspended listings leave `Universe` (it returns active listings only) and surface as unexplained exits unless a delisting action exists; suspension/resumption round-trips are not yet modelled as one holding period.
- The backtest is wired into its own trial register (above) but not into studies, diagnostics or any feature pipeline. A registered study over changing universes now has a genuine point-in-time stock dataset path — two-source [listing identities](listing-identity.md) converted to prices by [ListingPrices](listing-prices.md) — but its availability-at-capture rule means history only accumulates from the first capture forward; the multi-year capture vintages a study needs do not exist yet, and the French panels remain fixed-universe research-only inputs.
