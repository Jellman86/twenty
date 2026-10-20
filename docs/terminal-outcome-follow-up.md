# Declared ABST terminal-window follow-up

Declared 20 October 2026 before the request. The [fixed six-capture review](terminal-outcomes.md) found that ABST's original request ended on 2020-11-28, while [Nasdaq ECA 2023-425](https://www.nasdaqtrader.com/TraderNews.aspx?id=ECA2023-425) states final trading on 2023-07-26, merger completion before market open 2023-07-27 and suspension on 2023-07-28.

One bounded follow-up request is declared:

- Symbol **ABST**, start **2010-01-01**, end **2023-08-27** (completion plus 31 calendar days).
- Use the existing deployed app's quota-counted Tiingo capture endpoint, preserving the original start so an expanded history can be compared with the 22 earlier rows. One attempt; preserve a failure or empty response rather than replacing the symbol or silently repeating requests.
- Latest status before declaration: last attempt 2026-10-19, 3,664,688 recorded monthly bytes, seven unique symbols, no source pause/error. ABST is already counted. The server reserves at most 8 MiB for this request and enforces its existing daily/monthly/pacing limits; no key is read or copied.
- Copy only the ABST capture observation log and named hash-verified price objects for offline review. Preserve the original six-capture archive and report.
- Compare final-session presence, post-trading dates, endpoint/earlier-segment coverage and overlapping rows against the prior capture. Run the same source-bound audit without modifying the reviewed dates or consideration to fit the response. Record failures and differences.

This is an ingestion/coverage audit. No model trial, final holdout or terminal action import is planned. The fixed-sample failures remain recorded regardless of the follow-up result. Raw histories and generated full reports stay private; commit source/capture hashes and original review notes.

## Outcome

The declaration was committed as `df14ee8a035c5fdea408c4f77ef84597accad45f` before the capture. One initial app request was rejected with `same-origin-required` because the required `X-Twenty-Request` transport header was omitted. The server's vendor-attempt timestamp and quota remained unchanged. Correcting that documented app header produced exactly **one vendor request**, captured at 2026-10-20T17:47:04.9175469Z, without changing the symbol/window or introducing another candidate.

The response contains **690 rows, 2020-10-28 to 2023-07-27**. All 22 rows from the original capture are still present, with every returned field unchanged. The earlier producer segment remains absent under this symbol, even though the request starts in 2010. The corrected window reaches the reviewed final session, but also includes **one row on 2023-07-27**, after Nasdaq's stated 2023-07-26 last trading date. The terminal audit therefore still exits 1, now with `post-last-trading-bars` for ABST instead of a request-window shortfall. It does not change the reference dates to accommodate the vendor row.

[Compact follow-up evidence](abst-terminal-follow-up.json) binds the new capture/payload, unchanged reference bundle, repeated byte-identical report and prior-row comparison. The original six-capture report is preserved. With only ABST refreshed, the six-symbol audit now records five post-last-trading rows across four symbols. This remains a failed coverage audit, not accepted terminal-return evidence.

The one vendor request added **167,962 bytes** to recorded monthly use (3,832,650 total), left the unique-symbol count at seven and consumed one daily request. No automatic backfill, retry loop, model trial, genuine final evaluation or broker call was started.

Manual shape inspection of the five post-trading rows finds flat raw OHLC in all five, zero volume in three, and nonzero volume in AAWW's April row and ABMD's January row. Thus simply dropping zero-volume rows would leave two unexplained later rows. Those patterns do not establish their meaning or justify treating them as executable sessions or cash proceeds. Raw prices remain private; a provider explanation is still needed.
