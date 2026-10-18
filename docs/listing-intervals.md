# Listing intervals from daily directory captures

Implemented 18 October 2026 on `dev`, advancing [#5](https://github.com/Jellman86/twenty/issues/5). The daily listing directories already captured by the [collectors](collectors.md) — `nasdaq-listed`, `nasdaq-other` and `tiingo-tickers` — now convert offline into observed listing intervals:

```sh
dotnet run --project src/Twenty.Data -c Release -- listing-intervals --archive /private/captures --output /private/listings
dotnet run --project src/Twenty.Data -c Release -- listing-intervals --archive /private/captures --output /private/listings --sources nasdaq-listed
```

Output is an immutable hash-named `<id>.csv` plus `<id>.audit.json` in the private output directory (converter `listing-intervals-v1`). Rebuilding from the same captures reproduces the same ID. A named source with no captures fails (`listing-capture-missing`) rather than silently producing a partial universe. Raw captures and outputs stay outside Git.

## What an interval means

One row per interval: `source,symbol,exchange,asset_type,price_currency,security_name,producer_start_date,producer_end_date,first_seen_utc,last_seen_utc,closed_absent_at_utc,snapshots_present,status,name_changed,producer_dates_changed`.

- `first_seen_utc`/`last_seen_utc` are **Twenty capture times**, never listing effective dates. A symbol in the first capture may have listed long before collection started; history before the first capture cannot be backfilled from these files.
- `closed_absent_at_utc` is the first later snapshot **without** the segment (`absent-from-later-snapshot`). It is not a delisting date, and no terminal proceeds are inferred.
- A gap of more than 7 days between snapshots of a source closes every open interval as `continuity-gap` and reopens fresh intervals afterwards. Presence is never claimed across an unobserved gap. Gaps are listed in the audit.
- A symbol that disappears and reappears starts a **new** interval. Reappearance is never merged into the old interval, because tickers are reused.
- Unchanged (304) captures count as presence evidence for that day, as elsewhere in the capture store. The same capture instant naming different bytes is rejected (`listing-snapshot-conflict`) instead of being ordered by guesswork.
- Still-open intervals end as `present-at-final-snapshot`.

Segment identity is `(exchange, symbol, producer startDate, assetType, priceCurrency)` within one source. Producer end dates come from the latest snapshot in the interval: an end that only extends is coverage growing; an end that shrinks, appears or vanishes sets `producer_dates_changed`. A revised name sets `name_changed` and the latest name is kept. A revised key field (start date, asset type, currency) closes the old segment and opens a new one — revisions stay visible rather than rewriting history.

Intervals are **per source**. Nasdaq and Tiingo rows for the same company are never merged: there is no reconciliation contract yet, and the same ticker can denote different securities across sources and time.

## Source formats, as actually observed

Parsers were verified on 18 October 2026 against one live capture of each directory (private scratch archive; 122,262 intervals from one snapshot per source). The genuine files required four behaviours that authored fixtures alone would have missed:

- Nasdaq security names can carry trailing padding. Names are trimmed; symbol identity fields remain strict and untrimmed.
- Tiingo's `supported_tickers.csv` is a **history-segment** list, not a current-listing list: 109,069 rows including delisted segments, mutual funds and non-USD currencies. One ticker recurs over disjoint producer date ranges when reused (for example a 2015–2018 stock segment and a 2026 ETF segment under one ticker). The producer-start key keeps those segments apart.
- The same Tiingo file repeats a few rows byte-identically (9 extra copies in this capture). Those are deduped and counted per capture as `exactDuplicatesDeduped`.
- It also repeats a segment within one snapshot differing only by end date (a one-day predecessor row beside the continuing row; 69 rows in this capture). The latest end is kept and the collapse counted as `segmentRowsCollapsed`. Rows differing by asset type or currency are never collapsed into each other.

Nasdaq test issues are excluded and counted (8 and 29 in this capture). Nasdaq other-listed exchange codes are recorded raw and undecoded. Exact headers, the `File Creation Time` trailer, row shapes, flags and producer date order are all fail-closed; every capture is re-run through its collector validator before parsing.

## Limits — what this is not

- Not a certified point-in-time universe and not broker/ISA eligibility evidence. It records observed directory presence, with no company identity, CIK, calendar or denomination, so it does not yet emit [v1 contract](data-contract-v1.md) `Listing` observations.
- No delisting dates, terminal outcomes, symbol-change chains or cross-source identity. Disappearance can also mean a venue move, a directory error or a symbol change.
- Tiingo producer start/end dates are coverage claims from one vendor's current vintage, not independently audited listing dates. Nasdaq directories carry no dates at all beyond the file creation stamp (kept raw in the audit; its timezone is unstated and never converted).
- Coverage starts at the first capture. The point-in-time value compounds only as daily captures accumulate on Quark.

Next steps toward a stock panel remain those in [the real-data milestone](real-data-milestone.md). The [listing audit](listing-audit.md) now samples declared symbols against these intervals — producer ends versus last traded bars, directory disappearance, renames, segment restarts, and split/dividend adjustment consistency — while terminal proceeds and security identity stay unresolved. Still to decide: how intervals plus audited prices become v1 `Listing`/`Membership` observations with explicit identity evidence.
