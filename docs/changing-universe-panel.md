# Changing-universe panel

Implemented in `Twenty.Core.Research.ChangingUniversePanel` (`changing-universe-v3`). It is the bridge between the [v1 point-in-time dataset contract](data-contract-v1.md) and research returns over a universe whose membership changes: listings enter, leave, split and delist. The older [`MonthlyPanel`](../src/Twenty.Core/Research/MonthlyPanel.cs) requires every symbol present in every month, which is survivorship-biased by construction; this builder exists so future stock-level work does not inherit that bias. It computes returns and settlement metadata for the backtest described below; it does not select holdings, size trades or approve any strategy. All test inputs are authored **synthetic fixtures**, not market data.

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
- **Terminal stock covers a simple merger consideration.** A `TerminalOutcome.Stock` delisting realizes `exchangeRatio × successorRawClose(exit) / rawClose(t) − 1` using the successor's raw close at the exit decision. The validator requires a stock outcome to carry a known successor listing and a positive ratio (`terminal-stock`); the panel additionally fails closed when either raw close is missing (`universe-panel-terminal-price`, `universe-panel-terminal-successor-price`) or the currencies differ (`universe-panel-terminal-currency`), and applies the no-intervening-action guard to the original and successor (`universe-panel-terminal-complex`). Stock outcomes cannot contain positive cash or name their own listing as successor. Partial cash/stock mixes, spinoffs and suspension round-trips are not modelled.
- **Missing terminal proceeds fail the whole build** (`universe-panel-terminal-missing`). Dropping a delisted holding is exactly the survivorship bias this type exists to prevent.
- **Knowledge lag is honoured.** A delisting effective before a decision but only *available* after it still explains the subsequent exit: the name remains eligible at the uninformed decision, carries its last close, and realizes the terminal return in the following period.
- A price-currency change for a continuing listing fails (`universe-panel-currency-change`); quote-unit (pence/major) changes are normalized by denomination.

`ReturnStatusCounts` and `HasGaps` summarize disclosure: any `ExitedUnexplained` or `MissingPrice` entry sets `HasGaps`. A simulator consuming this panel must refuse gaps or adopt an explicitly documented exit policy; averaging only `Priced` entries reintroduces the bias. The panel `Sha256` covers builder version, dataset/universe identity, staleness bound, memberships, every classified return and stock-settlement successor/ratio/raw-price terms, so a later registry can bind trials to the exact panel.

## Backtest over the panel (`UniverseBacktest`, added later the same day)

`UniverseBacktest.Run` (`changing-universe-backtest-v3`) steps the existing decimal `NotionalPortfolio` through the panel with three non-learned baselines: equal-weight over every holdable listing, 12-1 momentum (compounded `Priced` period returns over 11 periods skipping the most recent; fixed constants, not searched), and cash. At each decision the holdable set is the month's eligible listings **minus** its `UnpricedListingIds`: a name whose price was not observable at that decision cannot be bought there, however briefly stale its price was. (A previously held name that is unpriced at the *next* decision would have already produced a `MissingPrice` gap in that period, which the backtest refuses, so the exclusion cannot silently drop a held holding.) The backtest **refuses gap panels** (`universe-backtest-gaps`): a portfolio cannot be stepped honestly through a month in which a possible holding has no return. Momentum candidates need a complete priced trailing window, so entrants are excluded until one exists — a disclosed selection rule. The report binds the panel `Sha256`, dataset and universe ids and synthetic flag, and states its limits in-band: local-currency returns with no FX conversion (GBP notionals are an accounting unit, not achievable GBP wealth), zero/cash terminal proceeds and bounded stock-only successor transfers, fractional full-rebalance accounting. Tests hand-verify the first equal-weight period, the −23.5% four-name terminal month, the entrant's first momentum eligibility, gap/short-history refusal, cost drag, and that a mid-run cash terminal exit settles without a phantom holding, while simple stock settlement carries successor exposure and a subsequent rebalance sale pays ordinary costs. This is an engineering baseline over synthetic fixtures; the registered-trial adapter is described below, and no market claim is made.

## Offline CLI

```sh
dotnet run --project src/Twenty.Data -c Release -- universe-backtest --dataset <input.json> --universe <id> --decisions <iso8601,iso8601,...> [--max-price-age-days 45 [--top-k 3]]
```

Decisions must carry explicit UTC offsets. The command prints one JSON object: panel identity/hash, month/period counts, per-status return counts and `hasGaps`, plus backtest summaries (default £20/month and default research costs) when the backtest runs. A refused backtest (gaps, insufficient history, invalid config) still prints the panel block with a `refusal` code and exits 1; dataset-selection failures exit 1 with the structured issue code. Smoke-verified against an independently generated JSON copy of the test scenario: 17 evaluated periods, £340 contributions, cash exact, equal-weight maximum drawdown −23.5% at the terminal month.

## Registered trials

Universe backtests can now be *registered trials* rather than ad-hoc CLI output. `ResearchStore` (partial `UniverseRuns`) keeps a separate `universe_runs` register in the same SQLite database: v1 datasets are imported content-addressed (`universe-datasets/<sha256>.json`, via the `imports/` inbox over HTTP), and `ExecuteUniverseRun` inserts the attempt as `started` **before** computing, then records exactly one terminal state — `succeeded` with panel and report hashes (report stored immutably at `runs/<id>/universe-report.json`), or `failed` with the refusal code. Gap-panel and missing-terminal attempts therefore count as trials. Replaying a request id returns the original entry whatever its status; changing the semantics under the same id is `request-id-conflict`; a process death mid-trial leaves `started` rows that store opening marks `interrupted`. HTTP surface: `POST /api/research/universe-runs/import-dataset`, `POST|GET /api/research/universe-runs`, `GET /api/research/universe-runs/{id}[/report]`. Execution is synchronous in v1 (the store method runs the backtest before returning), bounded by the 8 MiB dataset cap and 600-decision limit. This register is deliberately separate from the frozen study machinery: no promotion, holdout or diagnostic consumes universe runs yet, and there is no UI. The offline CLI above remains unregistered — its output is exploratory and must not be quoted as trial evidence.

## Limits and next steps

- Returns are decimal ratios of adjusted closes; they inherit whatever adjustment quality the dataset asserts. Nothing here validates a vendor's adjustments against independent records (still open in [#5](https://github.com/Jellman86/twenty/issues/5)).
- Terminal-cash handling covers only the simple no-intervening-action case; `TerminalStock` covers a simple stock merger (successor raw close at exit times the exchange ratio, same no-intervening-action guard and currency checks), and spinoffs, partial cash/stock mixes and suspensions remain unimplemented and fail closed or classify as unexplained exits.
- Suspended listings leave `Universe` (it returns active listings only) and surface as unexplained exits unless a delisting action exists; suspension/resumption round-trips are not yet modelled as one holding period.
- The backtest is wired into its own trial register (above) but not into studies, diagnostics or any feature pipeline. A registered study over changing universes has a fixture-validated stock dataset conversion path — two-source [listing identities](listing-identity.md) converted to prices by [ListingPrices](listing-prices.md) — but its availability-at-capture rule means history only accumulates from the first capture forward; the multi-year capture vintages a study needs do not exist yet, and the French panels remain fixed-universe research-only inputs.

## Settlement correction (20 October 2026)

The panel can report a simple stock-merger valuation, but the backtest now refuses any panel containing `TerminalStock` with `universe-backtest-stock-settlement`. The earlier implementation converted the value of successor shares into cash without a sale, transaction costs or subsequent successor exposure. A share-transfer ledger or an explicit, costed liquidation policy is required before such backtests are supported. Cash and zero-proceeds settlement remain implemented.

Builder and engine versions advance to v2. Existing status numbers remain `ExitedUnexplained = 4` and `MissingPrice = 5`; stock is appended as `6`, preserving the pre-extension numeric serialization contract. Immutable artifacts from the intervening local implementation must not be silently reinterpreted or rewritten.


## Successor exposure settlement (20 October 2026)

The v3 builder includes `StockSettlement` on each stock-terminal return: successor listing id, exchange ratio, original raw close and successor raw close. The panel hash binds these terms even if different terms happen to produce the same return. The v3 backtest transfers the realized value from the original holding to the successor **after** applying period returns, merging with any existing successor exposure. The original disappears; no cash, sale, purchase fee or traded notional is invented by the exchange. Reports record actual held transfers separately as `PortfolioStep.StockTransfers`. An unheld original produces no transfer.

This is exact exposure accounting within the existing fractional-notional model, not a broker share ledger. For example, 100 units of exposure at an original raw close of 20 correspond to five original shares in those local notional units. A 0.5 exchange ratio at a successor close of 50 delivers 125 units of successor exposure. A subsequent 20% successor return grows it to 150. An explicit sale at a 1% per-side cost then leaves 148.5 cash. Fractional-share rounding, cash in lieu, settlement delays, taxes specific to reorganisations and broker conversion of shares remain outside scope. GBP exposure units still do not supply a local-currency/GBP FX conversion.

The successor must be eligible and have an observable adjusted endpoint price at the exit decision, including when it is a new entrant; otherwise the backtest refuses `universe-backtest-stock-successor-coverage`. This conservative restriction ensures its next period has a return or an explained terminal outcome. The transfer may exceed allocation caps; the next scheduled full rebalance retains, trims or sells the resulting exposure under normal costs/minimums. It does not retrospectively choose the original allocation from merger outcomes. Multiple originals may deliver one successor. Same-period chained exchanges and overlaps with cash/zero exits are refused by the ledger (`invalid-stock-settlement`); unsupported corporate actions remain subject to the panel's existing guards.

Regression cases hand-check value conservation, existing-successor merging, no repeated application of successor returns, retained exposure, later sale costs, multiple originals, unheld originals, new entrants, unavailable/ineligible successors, invalid-transfer atomicity and settlement-bound panel identity. All settlement financial inputs are authored synthetic fixtures. No new market capture, model trial, holdout access or broker action was performed. The earlier v2 refusal above is retained as the historical correction record; existing immutable v2 reports are not rewritten.


## Cross-register research controls (20 October 2026)

Superseded details: the [21 October registered study workflow](universe-studies.md) implements candidate selection and one final pair, adds exact UTC exposure/claim boundaries and shares one computation lease with the model worker. Legacy day-only exposure is widened conservatively; the original date-only description below records the earlier checkpoint. Notional results still cannot nominate an investment or enable broker execution.

Follow-up: [retrospective universe diagnostics](search-diagnostics.md#changing-universe-families-and-complete-disclosure-20-october-2026) now consume registered momentum/equal-weight/cash paths and disclose both trial registers. Registered changing-universe candidate selection/final evaluation remains separate work. Earlier sections stating that no diagnostic or UI exists describe the earlier checkpoint.

Universe trials now share the workspace's 1,000-trial budget with ordinary model runs and registered study jobs, including the two slots reserved for each pending study final pair. Study declarations retain a `PriorUniverseTrials` snapshot alongside ordinary prior-trial metadata. This improves local disclosure; it does not compute a whole-programme multiple-testing correction.

Before a genuine universe trial is admitted, the store checks active genuine study reservations under the same admission lock used to register studies. Its information window covers **all** decision periods, including momentum warm-up, plus the conservative maximum-price-age lookback before the earliest decision. The start is projected to a UTC date and padded by the declared age bound; the end is the last UTC decision date. Date-only outcome overlap follows the existing half-open study convention. The padding may reject an overlap even when a particular payload happened to use a newer price. Policy conflicts are refused before trial creation or computation; admitted failed/interrupted attempts remain counted.

New trial entries persist `InformationStart`, `InformationEnd`, `IdentityScheme` and `ResearchSha256` before computation. A later genuine study cannot reserve overlapping outcomes already admitted through the universe register, even if the trial failed. A legacy genuine universe entry without retained window metadata conservatively refuses new genuine study registration/final admission (`study-universe-exposure-unknown`); neither a narrower window nor a fresh engine identity is invented for it. Existing request replays still return their original entries. Synthetic trials are excluded from genuine-data reservation checks but count toward capacity and prior-trial disclosure.

The checks protect workspace study reservations; they do not certify absence of manual/offline inspection or activity in other workspaces. Universe backtests remain exploratory registered baseline trials, without study-bound candidate selection, final evaluation or promotion. Regression financial values are authored fixtures; deliberately false provenance flags exercise guard behaviour only and are never historical evidence. Three regression cases reproduced the missing cross-register guards before the correction.


## Immutable universe request evidence (20 October 2026)

New trials retain the complete resolved `UniverseRunRequest` as `runs/<id>/universe-request.json` before return computation, including decisions, staleness bound, contribution/allocation configuration, costs, dataset/universe identity and request UUID. `RequestArtifactSha256` binds these exact bytes; `RequestSha256` retains its existing semantic-request meaning. `GET /api/research/universe-runs/{id}/request` returns the original bytes after checking both bindings, including for failed trials. Request and report writes share the ordinary model-report 8 MiB per-artifact and 256 MiB workspace check under the store's admission/write lock. This is a bounded research-artifact check, not a general filesystem quota for independent collectors.

A repeated UUID returns the original trial before reopening dataset inputs, so removing a dataset cannot turn a replay into a new computation or hide its original outcome. Missing or tampered request evidence is never repaired by replay. Legacy trials without a retained request hash report `universe-request-unavailable`; old completed reports remain readable. This is provenance/reproducibility support, not automatic rerunning, a historical-availability certificate or permission to consume reserved data.


## Workbench history inspection

The Growth lab now includes changing-universe trial history with stored-success reports, retained failure codes, provenance classification and information windows. Opening a record performs reads only. Successful reports show equal-weight/momentum/cash summaries and any successor exposure transfers. Values are labelled as notional accounting units without an achievable-GBP claim or an invented market benchmark. Legacy entries without request evidence remain explicit. The shared `/api/research/trials` summary exposes total, model and universe counts; the model history uses its own count rather than suggesting its latest rows contain all workspace trials.

Browser verification uses authored, intercepted responses against the built static bundle: success/transfer inspection, a failed trial, late-response selection races, empty/error states and a 390px viewport. The shared browser test never submits a production research trial. T3's native preview could not open because host browser libraries were missing; the existing authenticated shared Playwright service provided the check without an administrative host change.
