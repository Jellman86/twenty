# First real-data integration

Implemented 18 October 2026 on `dev`. No paid data, new account or broker order is created by these commands. Raw source files, API credentials, derived panels, audits and reports stay in private storage.

## Tiingo Starter

The owner supplied a free-account key. It is recorded outside both Git repositories under the workspace `.agents/nevercommit/twenty/` directory (owner-only permissions), and installed as a Dockhand encrypted `TWENTY_TIINGO_KEY` variable. Compose contains only the variable reference. Alternatively use a private mounted file named by `TWENTY_TIINGO_KEY_FILE`; setting both routes is refused. Neither the UI nor status endpoints return the key. Authentication uses an `Authorization: Token` header, never a URL parameter. [Official connection instructions](https://www.tiingo.com/documentation/general/connecting).

The current [Starter tier](https://www.tiingo.com/about/pricing) lists 50 requests/hour, 1,000/day, 500 unique symbols/month and 1 GB/month. Twenty enforces a minimum 90-second interval (at most 41 attempts in any inclusive hour), 900 requests per UTC day, 500 symbols per UTC calendar month and an 800,000,000-byte monthly response budget. The interval also bounds requests over a rolling day. Limits and symbol usage persist in the private capture store, and attempts reserve quota **before** sending. Failed/ambiguous requests retain the maximum 8 MiB response reservation; completed bounded responses replace it with actual received bytes. A restart cannot refund an uncertain request. Monthly/day boundaries reset only their corresponding counters. Rate limiting pauses requests for 24 hours; there are no automatic retries or automatic bulk backfills.

These are this installation's counters, not Tiingo's authoritative account usage. Other tools using the same key consume the same provider allowance. Keep the key exclusive to Twenty. The initial development access probes consumed only a handful of requests; no whole-market download was attempted.

Settings shows configuration, last success and recorded usage, with an explicit read-only authentication check. The API offers:

- `GET /api/ingest/tiingo`: status and quota counts, no credential.
- `POST /api/ingest/tiingo/check`: one paced authentication request.
- `POST /api/ingest/tiingo/history`: `{ "symbol": "AAPL", "from": "2015-01-01", "to": "2026-10-16" }`; one bounded daily-history request and private exact capture. It returns counts/dates/hash, not price data. Writes use the existing sign-in/same-origin rules.

A symbol/history response must have ordered unique UTC dates, unique fields, valid OHLC relationships, adjusted close, volume, dividend and split values. Empty history is recorded as zero coverage. Unknown/bad payloads are refused; error bodies and authentication headers are never copied into public diagnostics. Redirects are disabled.

On 18 October, authenticated access returned 2,965 AAPL rows (2015-01-02–2026-10-16) and 943 BBBYQ rows (2020-01-02–2023-09-29). The latter establishes access to **one** delisted history, correcting the previous unknown. It does not establish complete inactive coverage, ticker identity or terminal shareholder proceeds. Neither symbol was selected as an investment recommendation. Prices have not been converted into a certified stock panel. Raw bars, dividends and splits still require corporate-action, calendar and delisting audits before a multibagger census or stock-selection study. [Official history fields and adjustment description](https://www.tiingo.com/documentation/end-of-day).

Offline commands read the same private key/file environment variables and use the same persistent quotas:

```sh
dotnet run --project src/Twenty.Data -c Release -- tiingo-check --archive /private/tiingo
dotnet run --project src/Twenty.Data -c Release -- tiingo-history AAPL --from 2015-01-01 --to 2026-10-16 --archive /private/tiingo --capture
```

Do not run a separate collector with the same key/counters alongside the app. Use the app endpoint for Quark requests. No price scheduler is enabled.

## French industry return panel

The first usable historical model input is a **research portfolio proxy**, not individual stocks or broker instruments. It uses the first value-weighted monthly section of French's ten-industry archive, a separate market benchmark reconstructed as `Mkt-RF + RF`, and ECB reference USD→GBP rates. Long–short SMB/HML factors are not treated as buyable assets. Equal-weighted tables and annual summaries are not concatenated into monthly returns. [Industry construction](https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/Data_Library/det_10_ind_port.html), [market definition](https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/Data_Library/f-f_factors.html).

The requested window is fixed at January 1999–August 2026, before examining model results. January is the initial index/FX baseline, with no invented prior-month return. USD returns are percentages; the GBP index equals the cumulative USD index multiplied by current GBP-per-USD divided by the initial rate. Each month uses the last common GBP/USD ECB reference date on or before calendar month-end, at most seven days old; its exact date/rate is retained. No missing month, missing return sentinel, future FX date or stale fixing is silently filled. ECB fixings and US closes occur at different instants; this is an approximate reference-currency experiment, not executable close-to-close accounting.

```sh
dotnet run --project src/Twenty.Data -c Release -- capture-public --archive /private/captures --sources french-industry-monthly,french-factors-monthly,ecb-reference-history
dotnet run --project src/Twenty.Data -c Release -- french-panel --archive /private/captures --from 1999-01-31 --to 2026-08-31 --output /private/imports
```

The output is an immutable hash-named CSV, metadata and audit. They retain original capture URLs/hashes/retrieval times, conversion version, per-month FX choices and return inputs. The app's private `<data>/imports` inbox accepts only a 64-hex panel ID, checks the combined input hash, and imports through `POST /api/research/datasets/import` with `{ "panelId": "<hash>", "name": "French industry portfolios — research only" }`. Arbitrary paths and changed bytes are rejected.

The published history is a **retrieved/revised vintage**, with no invented historical publication times. French changed its underlying CRSP format from FIZ to CIZ in January 2025; CIZ compounds daily returns with ex-date dividend reinvestment, and historical returns can be revised. This is not an independent replication of constituent stocks. [Producer's vintage/methodology notes](https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/data_library.html).

Metadata marks the panel `researchOnly: true` and includes its reason. That flag travels into the dataset and report, blocks registered strategy promotion, blocks both training/current-panel forward signals, and blocks coordinator execution even with an external policy. Ordinary chronological development experiments and conditional scenarios remain available, clearly labelled. It cannot become evidence for an executable stock-selection strategy. The reserved final window stays unused for this milestone.

## Remaining stock-data work

Daily listing snapshots now parse into [observed listing intervals](listing-intervals.md) (18 October 2026); coverage compounds from the first capture and cannot be backfilled. Next: sample more delistings and renamed listings against those intervals; cross-check splits/dividends and terminal outcomes; acquire as-filed fundamentals with a genuine monitored SEC contact; then construct a point-in-time stock universe. A free Tiingo key removes an access dependency, not the statistical and accounting acceptance gates. No millionaire probability or profitable strategy is established here.
