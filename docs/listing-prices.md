# Price conversion evidence contract (`listing-prices-v1`)

Implemented 19 October 2026 on `dev`, advancing [#5](https://github.com/Jellman86/twenty/issues/5) and completing the step the [listing identity contract](listing-identity.md) explicitly left open: `Twenty.Ingestion.Listings.ListingPrices` converts captured Tiingo daily histories into [v1](data-contract-v1.md) `PriceBar` observations (raw and total-return-adjusted) plus split/dividend `CorporateAction` observations, over a validated two-source identity dataset. The result is the first dataset shape that a `ChangingUniversePanel` can price from genuine, forward-collected evidence.

```csharp
var result = ListingPrices.Convert(store, identity.Dataset, dataset-id);
```

Inputs are a `CaptureStore` holding `tiingo-history-<ticker>` response captures and the `DatasetFile` produced by `ListingIdentity.Convert`. Output is `ListingPricesResult(Dataset, BarsAdded, ActionsAdded, OutsideIntervalSkipped, Excluded)`; raw captures and datasets stay outside Git.

## The contract

- **Availability is capture time.** Every bar and action from one capture cites that capture's `ObservedAt` as both published and available, with `ObservedPublication` evidence. A vendor's current vintage becomes *effective* history — session dates back to whenever the symbol traded — but it can never be presented as point-in-time knowledge from before it was captured. `PointInTimeDataset.AsOf` therefore hides all prices before the capture instant, exactly as it hides identities (see the identity doc's availability consequence).
- **The latest capture per symbol wins**, mirroring the audit's capture selection. Two captures claiming the same latest instant with different content are unreconcilable (`price-conversion-capture-conflict`); re-converting a dataset that already carries prices is refused (`price-conversion-duplicate`) — a rebuild supersedes by dataset id instead, and a manifest already declaring the `tiingo-history` source cannot absorb another (`price-conversion-source-conflict`).
- **Two bases per session**: `PriceBasis.Raw` from the raw OHLC fields and `PriceBasis.TotalReturnAdjusted` from `adjOpen/adjHigh/adjLow/adjClose`. Bar ids are `<listingId>-raw|tra-<yyyymmdd>`. Session closes are 16:00 in the listing's exchange timezone (v1: `America/New_York` only), so the session date survives DST — a January close is 21:00Z and a July close is 20:00Z.
- **Adjustment consistency is a conversion-time exclusion.** Every adjusted field on a row must imply the same factor as `adjClose` (relative tolerance `1e-6`); one row describing two adjustments excludes that listing (`adjustment-inconsistent`). The parse layer only requires the adjusted fields to be present and positive — genuinely inconsistent rows are dropped from the dataset rather than failing the whole conversion, and the row-level step checks remain the [audit](listing-audit.md)'s job.
- **Corporate actions** come from the vendor's own `divCash`/`splitFactor` on each row: `CorporateActionKind.Dividend` with `CashPerShare` and currency, `CorporateActionKind.Split` with `SplitRatio`, effective at the session close. Ids are `<listingId>-div|split-<yyyymmdd>`. These are vendor claims verified only for internal consistency against the adjustment steps; they are not reconciled against exchange notices.
- **Rows outside the proven active listing interval are counted, not converted** (`OutsideIntervalSkipped`): a bar trading after the identity-proven delisting instant is either stale vendor data or a symbol-reuse successor — never silently attached to the identity. A listing whose capture is absent (`history-not-captured`) or whose rows all fall outside its interval (`no-prices-in-interval`) is excluded, and its identity rows are dropped with it: an untrusted or unknowable price never reaches a priced dataset.
- **The manifest** replaces the `identity-only` feature with `daily-prices`, adds a `tiingo-history` source snapshot (bundle hash of exactly the captures used, retrieved at the latest of them), and sets `CreatedAt` to the later of the identity's and the latest used capture for deterministic ordering.
- **Determinism**: conversion is a pure function of the identity dataset and the store's captures; test-identical inputs serialize byte-identically.

## Why a full backtest-over-converted-data test is not yet possible

The obvious end-to-end claim — capture histories, convert, backtest over years of genuine returns — is impossible *by design* with today's single-capture vintage, and the omission is deliberate rather than unfinished:

- availability-at-capture means the first genuine price appears at its capture instant;
- a `ChangingUniversePanel` decision needs a price no more than `maximumPriceAge` (≤ 92 days) old;
- the backtest needs 14 consecutive monthly decisions, hence ~14 months of *prior* captured history per decision.

One capture therefore yields at most one or two honest decision points, not a backtest. The registered pipeline test stops where honesty stops: captures → identity → conversion → point-in-time panel, asserting prices appear exactly at capture and the first monthly decision prices cleanly. Months of accumulated capture vintages are required before an honest genuine-data backtest exists; that accumulation is a wall-clock prerequisite, not a code gap. The panel builder's `UniverseBacktest`-level coverage continues to use authored synthetic fixtures, and the French panels remain fixed-universe research-only inputs.

## Validation

Eight tests in `tests/Twenty.Tests/ListingPricesTests.cs` cover: both bases at DST-correct session closes with point-in-time availability and manifest features; dividend/split action emission and outside-interval counting; adjustment-inconsistent exclusion that keeps the rest of the dataset; the duplicate/source-conflict/capture-conflict/empty refusals; determinism; `ParseHistory` rejecting legacy rows without adjusted OHLC; and the captures → identity → prices → point-in-time panel pipeline over an authored flat-price fixture. All inputs are **authored synthetic fixtures** shaped like genuine Tiingo responses (including the adjusted fields the strict parser now requires); no genuine capture was converted in the test suite, and the genuine Tiingo payload schema (`adjOpen/adjHigh/adjLow` present on every row) is what makes the strict contract safe.
