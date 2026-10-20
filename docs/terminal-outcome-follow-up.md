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

Pending at declaration.
