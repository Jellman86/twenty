# Listing identity evidence contract (`listing-identity-v1`)

Implemented 19 October 2026 on `dev`, advancing [#5](https://github.com/Jellman86/twenty/issues/5) and unblocking [#6](https://github.com/Jellman86/twenty/issues/6). This is the explicit identity-evidence contract the [listing intervals](listing-intervals.md) and [listing audit](listing-audit.md) deliberately did not mint: it converts per-source observed intervals into [v1](data-contract-v1.md) `Listing`/`Membership` observations for a **declared symbol sample**, under rules strict enough that a downstream consumer never has to guess what an identity assertion rests on.

```sh
dotnet run --project src/Twenty.Data -c Release -- listing-identity --archive <captures> --symbols AAPL,COST,... --dataset-archive <dataset-archive> [--dataset-id <id>]
```

Output is a content-addressed v1 dataset (via `DatasetArchive`, so storing revalidates the whole file) plus a JSON summary listing every included symbol and every exclusion with a stable reason code. Raw captures and datasets stay outside Git.

## The contract

Identity is asserted only on **two-source agreement** between the Nasdaq-listed directory (exchange-primary presence) and Tiingo's ticker segments (producer dates):

- **Active listing**: symbol present in the latest Nasdaq-listed snapshot, with a Tiingo segment that started on or before that observation and has not ended. Venue NASDAQ→`XNAS` only in v1; asset class must agree (stock/ETF); denomination must be USD.
- **Delisted listing**: a Tiingo segment has ended *and* a later Nasdaq observation lacks the symbol. Single-source endings are never asserted: producer-ended-but-still-listed, ended-but-Nasdaq-never-observed-afterwards, and present-on-one-source-only are all exclusions (`producer-ended-but-listed`, `ended-unconfirmed`, `single-source-present`).
- **Producer ends are coverage, not delistings.** Tiingo's end date is its last-data date: active tickers end at the capture frontier (genuinely 2026-10-16 for a 2026-10-18 capture). A segment counts as ended only when its end precedes the Tiingo capture by more than a documented 30-day coverage-lag tolerance (`CoverageLagDays`).
- **Symbol reuse and history**: each Tiingo segment is a distinct listing identity. Earlier ended segments our present-day Nasdaq captures cannot corroborate are excluded as `historical-segment-uncorroborated`, never silently merged into the current listing.
- **Conflicts exclude, with reasons**: `venue-conflict`, `asset-class-conflict`, `asset-class-unmapped`, `currency-unexpected`, `no-producer-start`, `tiingo-ambiguous-segments`, `nasdaq-ambiguous-segments`, `id-collision`, `not-observed`. An all-excluded run refuses to emit a dataset (`listing-identity-empty`).
- **IDs**: `listing-<slug>-<start yyyymmdd>`; company and security IDs derive 1:1 from the listing. That is an explicit non-claim: share classes, successor securities and CIK-level company identity are **not** resolved by directories, and this dataset must not be read as resolving them.
- **Evidence**: every observation cites the derived interval-artifact source (content hash of the interval CSV) with published/available at the latest capture instant used. Producer dates become *effective* history; they are never presented as knowledge available before collection began.

## The availability consequence (read this before using the output)

Because evidence is available only at capture time, `PointInTimeDataset.AsOf` returns an empty universe for any decision before the captures — which today means **every historical decision date**. This dataset cannot retroactively certify a 2015 or 2023 universe; nobody can harvest a vendor's current vintage and honestly backfill point-in-time membership from it. What it does provide is a forward-collectable identity layer: universes become legitimately knowable from each capture onward, and repeated captures will accumulate genuine interval evidence. The [price-conversion contract](listing-prices.md) can extend this dataset with captured prices (converter validation is currently synthetic) under the same availability rule, so a panel can be priced from captures; a multi-year backtest still awaits months of accumulated capture vintages, and, for any historical claim, evidence that was actually available then.

## Genuine smoke (19 October 2026)

Declared sample over the 18 October captures: AAPL, COST, MSFT, BBBYQ, DDC, AAIC, ABMD, NOSUCH (a deliberate non-symbol). Result (dataset `aab59f90…`, 10 observations, `synthetic: false`, standalone `validate` clean, panel builder loads it):

| Symbol | Outcome |
|---|---|
| AAPL, COST, MSFT | Active listings (effective from 1980/1986 Tiingo producer starts) + eligible memberships |
| ABMD | Active 1987-07-30 → delisted 2023-01-04 (producer end + observed Nasdaq absence) |
| BBBYQ | Excluded `venue-conflict`: Tiingo's sole segment is the successor OTC (PINK) listing; the vendor rewrote the symbol's history under the post-bankruptcy venue, so a NASDAQ delisting cannot be asserted from it |
| DDC | Excluded `venue-conflict`: current listing is NYSE American (out of v1 scope); its 1992–2017 same-symbol AMEX segment is a genuine symbol-reuse case |
| AAIC | Excluded `venue-conflict`: NYSE (out of v1 scope) |
| NOSUCH | Excluded `not-observed` |

## Second-order consequences

- The [changing-universe panel](changing-universe-panel.md) and its trial register can now load a *genuine* (non-synthetic) identity dataset end-to-end — verified in the smoke — but with no prices every return is missing, so backtests refuse honestly. Price conversion from captured Tiingo histories (with the audit's adjustment checks and the dividend sample's independent confirmation) is the next conversion step, and it inherits the same availability honesty: captured history bars are known at capture, not at their session dates, until a vintaged source says otherwise.
- Adding `nasdaq-other` venues needs a verified exchange-letter→MIC decoding table, not a guess; NYSE-family symbols (AAIC above) stay excluded until then.
- Forward capture cadence now matters scientifically, not just operationally: each new directory capture extends the window in which membership is genuinely point-in-time knowable. Gaps between captures become permanent evidence gaps (the interval layer already reports them).
- Vendor successor-symbol rewriting (BBBYQ) and decades-apart symbol reuse (DDC) are concrete cases the contract handles by exclusion. Any future deliberate inclusion needs security-level evidence (CIK/FIGI-class identifiers), not directory string matching.

## Limits

XNAS/USX scope is NASDAQ-listed USD stocks/ETFs only; company identity is unresolved by construction; terminal proceeds are never asserted here (a delisted listing carries no distribution — the panel will fail closed on it until proceeds evidence exists); and effective-from dates are single-vendor producer claims, cross-checked only for presence, not independently audited as listing dates.
