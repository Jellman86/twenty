# Nasdaq cash-equity session calendar

`NasdaqSessions` supplies the bounded `xnas-equities-2015-2026-v1` calendar for price conversion. It covers XNAS cash equities only, 2015-01-01 through 2026-12-31, in `America/New_York`. Weekend dates and 114 explicitly listed weekday closures have no session. The 25 listed early closes end at 13:00 local; other weekdays end at 16:00 local. UTC instants use the exchange timezone's DST offset.

The table is an original transcription of factual session dates from official Nasdaq annual calendars, with later notices applied. It is embedded in `Twenty.Ingestion` as `Listings/nasdaq-equity-sessions.v1.json`. The dataset's calendar reference records its version and the SHA-256 of the exact embedded bytes, replacing the identity layer's `unverified-v1` reference only for calendars actually used in conversion. Source URLs, private-original checksums and review method are in [source metadata](nasdaq-calendar-sources.json). Downloaded PDFs and rendered review images remain outside Git.

## Evidence and exceptions

Annual PDFs were visually reviewed because the holiday and early-close markers are graphical, not reliably represented by extracted text. Regular hours are supported by [Nasdaq's system-hours sheet](https://nasdaqtrader.com/content/TechnicalSupport/nasdaq_sys_hours.pdf); the final year's dates and 13:00 closes are in [the 2026 holiday schedule](https://www.nasdaqtrader.com/Trader.aspx?id=Calendar).

An annual PDF is not the final authority when a later notice supersedes it:

- 2018-12-05 is closed for the [Bush national day of mourning](https://www.nasdaqtrader.com/TraderNews.aspx?id=ETA2018-98).
- 2022-06-20 is closed for the [first Nasdaq observance of Juneteenth](https://www.nasdaq.com/articles/nasdaq-honors-juneteenth-through-our-continued-commitment-to-equality). The printed 2022 calendar predates that closure. June 18, 2021 remains a regular session.
- 2025-01-09 is closed for the [Carter national day of mourning](https://ir.nasdaq.com/news-releases/news-release-details/nasdaq-announces-closure-its-us-markets-honor-national-day-0), absent from the printed 2025 calendar.

Other useful controls are December 31, 2021 (regular trading, not a generic federal-holiday substitution) and July 2, 2015/2020 (regular equity closes, despite the following Friday's holiday). The adapter uses explicit dates rather than inferring equity hours from futures or bond calendars.

## Conversion behavior

`ListingPrices` advances to `listing-prices-v3`. A timezone match alone does not authorize another venue: a non-XNAS identity or non-New-York timezone is `price-conversion-calendar`. A bar on a weekend/holiday fails with `price-conversion-non-session`; a date outside the reviewed years fails with `price-conversion-calendar-range`. No fallback extrapolation or silent dropping of a non-session bar is performed. Rows outside an identity's active interval remain separately counted and skipped.

An intraday capture before the session's actual close is still `InProgressSkipped`; on an early-close date that boundary is 13:00 rather than 16:00. Dividend/split observations retain the converter's existing effective-at-session-close convention and actual capture-time availability. This change does not assert actual announcement, ex-date-open or payment instants.

These are retrospective session facts. They do not establish that an unscheduled closure was known before a historical decision, that a security traded all day, or that the official close could have been executed. Intraday halts, outages, instrument-specific suspensions, other venues, options/extended hours, settlement calendars and dates outside the stated range remain unsupported. General v1 dataset validation still accepts explicitly declared calendar references; this adapter is not a universal historical-calendar validator.

## Genuine audit and regression checks

Reconverted the same private genuine AAPL capture used by the [prior smoke](listing-prices.md#genuine-captured-history-smoke-20-october-2026), retaining the old immutable output. All 2,965 session dates from 2015-01-02 through 2026-10-16 match the new calendar: zero missing and zero unexpected sessions. There are 23 early-close dates in that span, changing exactly 46 raw/adjusted bar close instants by three hours. OHLC, volume and price availability remain unchanged; the dataset still carries 5,930 bars and 48 vendor corporate actions. Identical repeated conversion remains byte-identical. Exact hashes and early dates are in [calendar smoke metadata](nasdaq-calendar-smoke.json).

The Python wire audit independently constructs timestamps from the shared reviewed table, checks the table hash and venue, and rejects the old 16:00 timestamp when a reviewed early close is required. Sharing the source table means this cross-language check cannot detect a mistaken source transcription. The separate visual/source review and explicit date controls address that different risk.

.NET regressions cover regular/early closes in winter and summer, DST transitions, annual session/early-close totals, weekends, holidays, mourning notices, Juneteenth's adoption boundary, the 2021 year-end exception, unsupported years, venue mismatches, manifest provenance and a capture immediately before/after an early close. Python negative controls cover changed/missing bar/action fields, reviewed early-close times, table-hash mismatch and a non-session row. Financial unit inputs are authored fixtures; the bounded AAPL reconversion uses historical captured producer data. No model training, holdout or broker action is part of the audit.
