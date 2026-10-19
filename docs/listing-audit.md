# Listing audit: delisting, rename and corporate-action cross-checks

Implemented 18 October 2026 on `dev`, advancing [#5](https://github.com/Jellman86/twenty/issues/5). The offline `listing-audit` converter cross-checks a **declared symbol sample** between the [observed listing intervals](listing-intervals.md) and captured Tiingo daily histories, producing an immutable hash-named CSV plus audit JSON in private storage (converter `listing-audit-v1`):

```sh
dotnet run --project src/Twenty.Data -c Release -- listing-audit --intervals /private/listings/<interval-id>.csv --archive /private/tiingo --symbols AAPL,BBBYQ --output /private/listing-audits
```

The interval artifact must verify against its hash-named id or the audit fails (`listing-audit-interval-integrity`). Symbols are an explicit list (at most 50, duplicates and malformed tickers rejected); the tool never scans for a flattering sample. Histories are re-parsed with the capture-validation rules (`TiingoClient.ParseHistory`), the latest capture per symbol wins, and two different payloads at one capture instant fail (`listing-audit-capture-conflict`). Rebuilding from the same inputs reproduces the same audit ID. Raw inputs and outputs stay outside Git.

## What is checked per symbol

- **Terminal class**, combining the last history bar against the interval evidence date (latest `last_seen_utc` in the artifact) and against its own capture time, producer-ended status and Nasdaq directory presence at the final snapshot: `history-current`, `history-capture-stale` (bars reach their own capture date but that capture is old — not evidence of delisting), `delisting-candidate-proceeds-unresolved`, `history-ended-but-directory-present`, `history-ended-unresolved`, or `no-history-evidence`. A delisting candidate records its last captured close; **terminal shareholder proceeds are never inferred**.
- **Producer-end alignment**: the latest Tiingo segment end versus the last bar, with a declared 10-calendar-day tolerance (`history-continues-past-producer-end`, `history-ends-before-producer-end`, `producer-ended-and-history-ended`). Producer ends are coverage claims from the vendor's current vintage, not audited delisting dates.
- **Directory evidence**: `closed-absent-from-directory`, `history-continues-but-directory-absent`, `name-changed`, `producer-dates-changed`, `segment-restart` (same exchange and symbol with more than one producer start — the symbol-reuse candidate shape), `no-interval-rows`, `no-history-capture`. A rename flag records that the producer name changed; identity continuity is not resolved.
- **Split/dividend adjustment consistency**: for each date after the first, observed step = (adjClose[t−1]/close[t−1]) ÷ (adjClose[t]/close[t]). Expected step is 1/splitFactor on split ex-dates times (1 − divCash/close[t−1]) on dividend ex-dates, else 1. Steps whose relative error exceeds the declared 1% tolerance are recorded as `adjustment-step-mismatch` (event dates) or `unexplained-adjustment-step` (non-event dates), with the maximum relative error always reported. Recorded step detail is capped at 100 entries per list; the reported counts are uncapped totals. A dividend at or above the prior close cannot form a valid factor and is flagged `degenerate-corporate-action`. Events on the first captured bar are counted but cannot be step-checked. This verifies the vendor's adjusted series against its own corporate-action fields; it does not independently verify actions against exchange notices, and sub-tolerance dividend errors are by design not flagged. Same-day split+dividend uses the product of both factors on the pre-split close.
- **Cross-source requirement**: the `delisting-candidate-proceeds-unresolved` class requires Nasdaq directory sources to exist in the artifact and the symbol to be absent from their final snapshot. Ended Tiingo coverage plus ended Tiingo history alone — one vendor twice — stays `history-ended-unresolved` with a `no-nasdaq-directory-evidence` finding (tightened after the [Sol review](reviews/2026-10-19-sol.md)).

## First genuine sample (18 October 2026)

Intervals were rebuilt locally from fresh captures of all three directories (122,262 intervals, matching the [interval record](listing-intervals.md)) and audited against the two histories captured earlier that day, plus one deliberately history-less symbol. Sample declared in advance; no other Tiingo histories existed locally to add.

| Symbol | Class | Evidence |
|---|---|---|
| AAPL | history-current | 2,965 bars to 2026-10-16 = producer end; Nasdaq present; 1 split (4:1, 2020) and 47 dividends all consistent; max step error 0.013% |
| BBBYQ | delisting-candidate-proceeds-unresolved | 943 bars end 2023-09-29 = producer end; absent from Nasdaq directories; 1 dividend consistent; max step error 0.42% (sub-cent price rounding); last close $0.0789 recorded, proceeds unresolved |
| DDC | no-history-evidence | Two AMEX producer segments (1992 and 2023 starts) → `segment-restart`; no captured history, so nothing further can be checked |

No adjustment mismatches or unexplained steps were found in either genuine history at the 1% tolerance. That is consistency evidence for two files, not a coverage claim: 9,717 producer-ended Stock/USD segments (2020–2025) exist in the same interval artifact, and almost none have captured histories yet.

## Widened delisted sample (19 October 2026)

Declared before captures: from the same 122,262-interval artifact, Tiingo Stock/USD segments on NASDAQ/NYSE/AMEX with producer start on or before 2012 and producer end during 2020–2024 (569 eligible), sorted by symbol, first six — AAIC, AAWW, ABMD, ABST, ACB, ACC — captured through the deployed app's own Tiingo endpoint (server-paced, quota-counted) for 2010-01-01 to the sampled segment's producer end +31 days, then audited with AAPL and BBBYQ against a read-only copy of the capture store. No symbol substitution.

Across all eight genuine histories: 18,960 bars, 138 dividend events and 2 splits, **zero adjustment mismatches and zero unexplained steps** at the 1% tolerance (worst step error 0.42%, BBBYQ sub-cent rounding). Five of the six new symbols classified as delisting candidates with last bars on their producer end dates. Two anomalies are the informative part:

- **ABST** has two producer segments (2007–2020, 2020–2023). For a window starting 2010 the vendor returned only 22 rows beginning exactly at the second segment's start — the first segment's history was not served under this symbol. Cross-source class is still delisting-candidate (latest segment ended 2023-07, absent from Nasdaq), but segment-seam coverage like this is precisely what a survivorship-safe universe must not paper over.
- **ACB** also has two segments; the declared capture window targeted the sampled (first) segment, so its apparent 2021 staleness against the still-current second segment is window-induced, recorded here to keep that distinction explicit. Its 2014–2021 history and one reverse split are internally consistent.

## Independent dividend sample vs SEC declarations (19 October 2026)

The adjustment checks above verify Tiingo against *itself*. `scripts/audit_dividends_tiingo_sec.py` now checks its dividend amounts against an independent authoritative record: the issuer's own SEC filings (`us-gaap:CommonStockDividendsPerShareDeclared` in Company Facts). Declared sample: AAPL's 47 Tiingo cash dividends in the captured 2015–2026 history. SEC amounts are fiscal-quarter duration totals; each quarter takes its earliest-filed vintage (later filings republish comparatives split-adjusted — 10 quarters, at exactly the 7:1 and 4:1 split ratios), and fiscal Q4 amounts the issuer stopped tagging directly from FY2018 are derived as annual minus nine-month YTD and flagged derived.

Result: **45 of 47 vendor dividends match an SEC declaration exactly to the cent** (38 direct quarters + 7 derived Q4s; ex-dates 47–58 days before fiscal period end, median −50). The two residuals are explained, not discrepancies: August 2020's $0.82 straddles the 4:1 split (the FY2020 annual was first filed post-split, so no consistent-vintage derivation exists), and August 2026's $0.27 belongs to a fiscal year whose 10-K is not in the 19 October bulk vintage. The one unmatched SEC quarter predates the capture. This is single-symbol evidence that this vendor's dividend amounts — the inputs to its adjusted prices — agree with issuer filings; split factors, other symbols and terminal outcomes remain unsampled. Full results and payload hashes: [corporate-action-audit.json](corporate-action-audit.json).

## Limits and next steps

- Sample size is bounded by captured histories and the free-tier quotas that produced them; widening the delisted sample needs declared captures over time, not a bulk backfill. Daily interval captures on Quark also need time to accumulate before `closed-absent` and `name-changed` evidence exists in volume.
- Producer segment ends, directory absence and adjustment factors are all single-vendor claims until sampled against independent authoritative records (exchange notices, SEC filings). That sampling, plus terminal-outcome evidence (cash merger terms, liquidation distributions, OTC continuation), remains open in [#5](https://github.com/Jellman86/twenty/issues/5).
- Converting captured prices into v1 `PriceBar`/`CorporateAction` observations is now specified separately in the [price-conversion evidence contract](listing-prices.md); this audit still deliberately does not mint identity or prices.
