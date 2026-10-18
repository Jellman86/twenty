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
- **Split/dividend adjustment consistency**: for each date after the first, observed step = (adjClose[t−1]/close[t−1]) ÷ (adjClose[t]/close[t]). Expected step is 1/splitFactor on split ex-dates times (1 − divCash/close[t−1]) on dividend ex-dates, else 1. Steps whose relative error exceeds the declared 1% tolerance are recorded as `adjustment-step-mismatch` (event dates) or `unexplained-adjustment-step` (non-event dates), with the maximum relative error always reported. This verifies the vendor's adjusted series against its own corporate-action fields; it does not independently verify actions against exchange notices, and sub-tolerance dividend errors are by design not flagged. Same-day split+dividend uses the product of both factors on the pre-split close.

## First genuine sample (18 October 2026)

Intervals were rebuilt locally from fresh captures of all three directories (122,262 intervals, matching the [interval record](listing-intervals.md)) and audited against the two histories captured earlier that day, plus one deliberately history-less symbol. Sample declared in advance; no other Tiingo histories existed locally to add.

| Symbol | Class | Evidence |
|---|---|---|
| AAPL | history-current | 2,965 bars to 2026-10-16 = producer end; Nasdaq present; 1 split (4:1, 2020) and 47 dividends all consistent; max step error 0.013% |
| BBBYQ | delisting-candidate-proceeds-unresolved | 943 bars end 2023-09-29 = producer end; absent from Nasdaq directories; 1 dividend consistent; max step error 0.42% (sub-cent price rounding); last close $0.0789 recorded, proceeds unresolved |
| DDC | no-history-evidence | Two AMEX producer segments (1992 and 2023 starts) → `segment-restart`; no captured history, so nothing further can be checked |

No adjustment mismatches or unexplained steps were found in either genuine history at the 1% tolerance. That is consistency evidence for two files, not a coverage claim: 9,717 producer-ended Stock/USD segments (2020–2025) exist in the same interval artifact, and almost none have captured histories yet.

## Limits and next steps

- Sample size is bounded by captured histories and the free-tier quotas that produced them; widening the delisted sample needs declared captures over time, not a bulk backfill. Daily interval captures on Quark also need time to accumulate before `closed-absent` and `name-changed` evidence exists in volume.
- Producer segment ends, directory absence and adjustment factors are all single-vendor claims until sampled against independent authoritative records (exchange notices, SEC filings). That sampling, plus terminal-outcome evidence (cash merger terms, liquidation distributions, OTC continuation), remains open in [#5](https://github.com/Jellman86/twenty/issues/5).
- Converting intervals plus audited prices into v1 `Listing`/`Membership` observations still needs an explicit identity-evidence contract; this audit deliberately does not mint one.
