# Market-data selection for issue #5

Reviewed 16 October 2026. This is a review of providers' public documentation only. Nothing was bought, no account was opened, no credentials were entered and no paid payload was inspected. One unauthenticated call to EODHD's documented public `demo` token for a delisted-symbol list returned `403 Forbidden`, so EODHD's coverage below rests only on its pages. Every claim cites the provider's own page. **Unverified** means the documentation does not settle the point and a sample or a written answer from the vendor is needed.

It builds on [data sourcing](data-sources.md), [data contract v1](data-contract-v1.md), [upstream contracts](upstream-contracts.md) and the [growth research plan](growth-research.md). Its price-data acceptance questions and audit examples still apply. A vendor that passes this desk review must still pass that audit.

GBP figures use the ECB reference rates for 15 October 2026 (USD 1.1208 and GBP 0.84610 per EUR, so £0.755 per US$1). Card FX and VAT are not included. Treat GBP amounts as approximate.

## What #5 needs

| Need | Why | Used by |
|---|---|---|
| Delisted securities with their full price history | Without them, wipeouts disappear and winners are overstated | Every family, especially A |
| What a holder received at delisting | Contract v1 treats unknown proceeds as `missing`, never zero | A |
| Raw prices plus splits, dividends and spin-offs | Total returns and the bars that orders would have used | A, C, the simulator |
| Point-in-time (as-reported) fundamentals with filing dates | Multibagger features must use only what was known at formation | A |
| Historical universe membership | Today's constituents are not historical members | A, C |
| US and UK coverage | Stocks ISA opportunity set | All |
| 25+ years of history | 5- and 10-year labels need several formation windows (growth plan, evidence limit) | A |
| Terms permitting private research and automated daily collection | The autonomous loop runs unattended | All |

## Comparison

The table compares the cheapest tier of each provider that comes closest to the needs above.

| Provider and tier | Delisted coverage | Delisting outcome | Corporate actions and adjustment | Point-in-time fundamentals | US / UK | History | Terms for this use | Price |
|---|---|---|---|---|---|---|---|---|
| **Sharadar** Core Bundle, full history, personal use | Yes. "active and delisted US public stocks" ([stocks](https://sharadar.com/docs/stocks)); vendor estimates 99% survivorship-free ([FAQ](https://sharadar.com/docs/faqs)) | Delisting date, delisting reason and acquirer (`contraticker`) only. No proceeds or terminal return documented ([actions](https://sharadar.com/docs/actions)) | Split-adjusted OHLCV, `closeunadj`, and `closeadj` adjusted for splits, dividends and spin-offs; daily delivery 17:30 and 23:30 ET ([stocks](https://sharadar.com/docs/stocks)). Actions table covers splits, dividends, spin-offs and ticker changes ([actions](https://sharadar.com/docs/actions)) | Yes. ARQ/ARY/ART "excludes restatements", indexed to the SEC filing date ([fundamentals](https://sharadar.com/docs/fundamentals)). No accession number documented (**unverified**) | US only. No UK | Prices from December 1997, fundamentals from January 1998 | Personal licence for one natural person. FAQ: includes "research, backtesting, and automated trading of one's own account" ([FAQ](https://sharadar.com/docs/faqs)). No redistribution; delete data within 30 days of ending ([terms](https://sharadar.com/terms)) | $69/month or **$499/year (~£377)** ([pricing](https://sharadar.com/pricing)) |
| Sharadar Prices only, full history | As above | As above | As above, plus funds and the `sp500` membership table | None (pair with SEC EDGAR) | US only | December 1997 | As above | $39/month or **$299/year (~£226)** |
| **EODHD** All-in-one (personal) | Delisted symbols via `delisted=1` per exchange; the parameter is generic and the documented example is Warsaw. Fundamentals, dividends and splits only for delistings after 2018; EOD only before 2018 ([delisted](https://eodhd.com/financial-apis/delisted-stock-companies-data-2), [exchanges](https://eodhd.com/financial-apis/exchanges-api-list-of-tickers-and-trading-hours)). LSE delisted coverage **unverified** | Not documented | Raw OHLC plus `adjusted_close` for splits and dividends ([EOD](https://eodhd.com/financial-apis/api-for-historical-data-and-volumes)) | Statements carry `filing_date`; whether restatements overwrite earlier values is not stated (**unverified**). Minor companies: last 6 years and 20 quarters only ([fundamentals](https://eodhd.com/financial-apis/stock-etfs-fundamental-data-feeds)) | US and LSE (`BP.LSE` from 1988 in the vendor's sample). Pence versus pounds not documented | US common stocks from 1962 | Personal use is "for their own personal investment activities"; data "not appropriate for trading purposes"; automation and retention not addressed ([terms](https://eodhd.com/financial-apis/terms-conditions)) | $99.99/month or **$999.90/year (~£755)**. EOD-only Historian $199/year (~£150), without fundamentals ([pricing](https://eodhd.com/pricing)) |
| **Norgate** US Stocks Platinum | Delisted from 1990, with historical index constituents ([packages](https://norgatedata.com/stockmarketpackages.php)) | `LastQuotedDate` only. No terminal return documented ([content](https://www.norgatedata.com/data-content-tables.php)) | Splits, dividends and capital events; adjustment method selectable | No. "last-reported (current) fundamentals only" ([content](https://www.norgatedata.com/data-content-tables.php)) | US, Australia, Canada. No UK stocks | 1990 (Diamond: 1950, $787.50/year) | Licence not reviewed (**unverified**). Monthly subscriptions not offered | **$630/year (~£476)** |
| **Tiingo** Power (individual) | Not documented (**unverified**) | Not documented | Raw and adjusted OHLC, `divCash`, `splitFactor`; CRSP-style split and dividend adjustment ([EOD docs](https://www.tiingo.com/documentation/end-of-day)) | Separate add-on at an undisclosed price; 15+ years ([pricing](https://www.tiingo.com/about/pricing)) | US and Chinese stocks; UK not stated | "30+ Years" | "Internal use only" for individuals ([pricing](https://www.tiingo.com/about/pricing)) | $30/month or **$300/year (~£226)**, before fundamentals |
| **Massive** (formerly Polygon) Stocks Advanced | Ticker records carry `active=false` and `delisted_utc` from September 2003; aggregates for delisted tickers not documented ([tickers](https://massive.com/docs/rest/stocks/tickers/all-tickers)) | Not documented | Corporate actions included on all plans | "Financials & Ratios" from Advanced or a $29/month add-on; point-in-time not documented | US only | Pricing page says 20+ years for Advanced; the tickers page says all paid plans include all history. Conflict **unverified** | Stocks plans are "Individual use" ([pricing](https://massive.com/pricing)) | $199/month (~£1,803/year) |
| **CRSP** US Stock | "36,000 active and inactive securities" | Not stated on the product page (**unverified** here; CRSP is the usual source of delisting returns) | Not reviewed | Not CRSP's product | US only | "Over 100 Years" | "delivered to licensees at academic institutions"; no individual route shown ([Morningstar CRSP](https://indexes.morningstar.com/research-data-products/crsp-us-stock-databases)) | Not published. Unavailable to Twenty without an academic affiliation |
| **Free**: SEC EDGAR, ECB, Open Source Asset Pricing | No complete free price history with delistings was identified | None | None for prices | EDGAR filings and XBRL facts by accession, already planned ([data sourcing](data-sources.md)) | US filings; ECB FX | EDGAR XBRL from the 2009–2011 phase-in (**unverified** here) | Public; SEC access rules apply | £0 |

### What the comparison shows

- **No candidate documents delisting proceeds.** Sharadar gives the date, the reason and the acquirer. Every terminal outcome must start as `missing` and be resolved from primary sources (SEC merger filings and 8-Ks, through the existing collector), or stay `missing` and be excluded by the selection guard. This is the largest piece of work under any provider.
- **No affordable candidate documents UK delistings.** EODHD is the only one claiming LSE history, and its delisted coverage outside the US, its pence handling and its fundamentals for small companies are all unverified. Norgate and Sharadar do not cover the UK.
- **Only Sharadar documents point-in-time fundamentals** with filing-date indexing and an explicit exclusion of restatements, alongside delisted prices, at a price comparable to the price-only alternatives.
- **Only Sharadar's licence explicitly permits automated trading of your own account.** EODHD calls its data "not appropriate for trading purposes" and says nothing on automation. Tiingo and Massive say only "internal" or "individual" use.

## Recommendation

**Choose Sharadar Core US Equities Bundle, full history, personal licence, bought by the owner. Run a one-month audit first ($69, ~£52), then commit to annual billing ($499/year, ~£377, about £31 a month) only if the audit passes.** Restrict Family A and Family C to a **US-only** research universe. Label any UK work exploratory until a UK source passes the same audit.

Reasons:

1. It is the only reviewed option with delisted US prices, point-in-time fundamentals, historical S&P 500 membership and an actions table in one licence. That covers the growth plan's first steps (the multibagger census, Family C on broad-index data and Family A) without building an XBRL-to-features pipeline first.
2. History from 1998 gives about 19 annual formation dates (1998–2016) for 10-year labels and 24 (1998–2021) for 5-year labels, which is the minimum the growth plan's evidence limit can use.
3. The licence explicitly covers this use, including automated trading of the owner's account.
4. A US universe is relevant only to the extent Trading 212's ISA offers those shares. That is **unverified** here and needs the per-instrument check in the audit (step 5) and [upstream contracts](upstream-contracts.md).

**If the research budget is capped at about £20 a month:** buy Sharadar Prices only, full history ($299/year, ~£226, about £19 a month). Build fundamentals from SEC EDGAR by accession, which the project already plans. Family A then waits on the XBRL pipeline, whose XBRL history starts with the 2009–2011 phase-in, so it loses about a decade of formation dates. Do not buy the 5-year or 10-year tiers: they cannot form a single 10-year label with a full holding period plus training history.

**At £0:** nothing survivorship-free is available. Data can be collected going forward and labelled exploratory, but no backtest can support a profitability claim (data sourcing, "No primary full-market price provider is selected yet").

### Costs to weigh

- £377 a year is 157% of the £240 a year invested; £226 is 94%. The research programme will almost certainly cost more than any plausible excess return on £20 a month for years. Report both, as [data sourcing](data-sources.md) requires.
- The licence ends at cancellation. Within 30 days the owner must delete the data and any dataset that could reproduce it. Only derived work that "cannot reproduce" the data may be kept. Archived snapshots therefore cannot be reproduced after cancellation, so registered studies must record that their inputs are licensed and time-limited.
- **This is a public repository.** No Sharadar data, sample, fixture or reverse-engineerable derivative may be committed. Under §8 of the terms, publishing conclusions about the data's value or fitness needs Sharadar's written approval, which affects publishing the audit results described below. Keep the audit report private, or ask Sharadar first.

## Ingestion contract against data contract v1

This describes what a Sharadar adapter would have to do. Fields not documented on Sharadar's public pages are marked **verify on sample**. Each must be confirmed against the audit month's real payloads before code depends on it. No wire format here is guessed.

### Collection

- Use bulk table downloads for backfill and the daily REST delta (`lastupdated`) afterwards. Collect once a day after the documented 23:30 ET delivery.
- The API key is a **query parameter** (`?api_key=`) ([auth](https://sharadar.com/docs/auth)). Keep it in server-side configuration and strip it from URLs, logs, exception messages, cache keys and `SourceSnapshot.Location`, which v1 already requires to have no query string. Rate limits are not published, so reuse the shared limiter and back-off pattern from the SEC collector.
- Save each downloaded file unchanged under ignored `data/` as an immutable original, hash it with SHA-256 and record it as a `SourceSnapshot` with `LicenceReference = "sharadar-personal-2026-10"` and a `RevisionPolicy` that names the `lastupdated` rule below.

### Table-to-observation mapping

| Sharadar table | v1 observation | Rules |
|---|---|---|
| `tickers` | `Listing` | `ProviderIds` include `sharadar.permaticker` and `sharadar.ticker` (**verify on sample**: that `permaticker` exists and is stable across ticker changes). Denomination `USD`/`Major`. `Status = Delisted` and `EffectiveUntil` from the delisting date. A ticker change is a new `Listing` interval, never an edit of the old one. Securities other than common stock and ETFs are excluded, because `AssetClass` has only `Stock` and `Etf` |
| `stocks`, `funds` | `PriceBar` | Store one `SplitAdjusted` bar per session exactly as delivered. Derive a `Raw` bar only through the documented imputation (`closeunadj` gives the factor; Sharadar's FAQ shows the formulas), marked as derived in data-quality flags. **Do not** turn `closeadj` into a `TotalReturnAdjusted` bar: v1 forbids copying an adjusted close into a fully adjusted OHLC bar. Calculate total returns locally from raw bars plus `CorporateAction` dividends, and keep `closeadj` only as a cross-check. `SessionCloseAt` comes from a versioned NYSE/Nasdaq calendar, which is still unsourced |
| `actions` | `CorporateAction` | Split → `Split` with `SplitRatio` as new shares per old share (**verify on sample** which way round `value` is given). Dividend → `Dividend`, `CashPerShare` in USD. Ticker change → `SymbolChange` with `NewSymbol`. Spin-off → `SpinOff`. Delisting by acquisition → `Merger` with `SuccessorListingId` resolved from `contraticker`. Other delistings → `Delisting`. **`TerminalOutcome = Missing` for every delisting** until proceeds are confirmed from an SEC filing (`Cash`) or a liquidation with nothing paid out is confirmed (`Zero`); each resolution is a new revision citing the filing's accession |
| `fundamentals` | `FundamentalFact` | Accept only the ARQ, ARY and ART dimensions. Reject MRQ, MRY and MRT at import. v1 requires an `Accession`, which Sharadar does not document (**verify on sample**). Recover it by matching CIK, form type and filing date against SEC submissions from the existing collector. If no single filing matches, reject the row; never invent an accession. Restatements become new revisions with the new accession |
| (SEC submissions) | `Filing` | Comes from the SEC collector, not Sharadar. Supplies `AcceptedAt` for the fundamental facts' evidence |
| `sp500` | `Membership` | `UniverseDefinition(Kind = Research, Id = "sp500-historical")` with intervals from added and removed dates. It is a reference universe for Family C and benchmarks, not the Family A universe, which must be every eligible listing |
| (ECB) | `FxRate` | Existing ECB parser, `Purpose = Reference`. USD→GBP for reporting only; never treated as an executable broker rate |

### Clocks and revisions

- **Prices in the backfill:** `AvailabilityEvidence = ConservativePolicy`, `Precision = Day`, with a policy (proposed id `sharadar-eod-next-session-v1`) under which a session's bar is available from the next session's open. Sharadar's documented 23:30 ET delivery is today's practice, not proof of when historical bars were available.
- **Prices collected live:** `ObservedPublication`, with `FirstSeenAt` set to Twenty's own retrieval time.
- **Fundamentals:** availability comes from the matched SEC filing's acceptance time plus the existing SEC availability policy. Sharadar's filing-date key is only a cross-check. Sharadar itself warns that markets may have learned results earlier through 8-Ks, so the earlier 8-K date is a feature to record, not a reason to backdate the 10-Q or 10-K.
- **Revisions:** a change in `lastupdated` for an existing (listing, session, basis), action or fact key becomes a new revision linked by `RevisionOf`, never an overwrite. Price revisions after a corporate action are expected and must not be treated as corruption.

### Audit month (acceptance gate before annual purchase)

Use the cases in [data sourcing](data-sources.md#price-data-acceptance), drawn from the purchased data and checked against primary sources:

1. A cash acquisition, a stock-for-stock merger, a bankruptcy, a reverse split, a spin-off, a special dividend and a ticker change. For each, compare Sharadar's actions with the SEC filings and record what `value` and `contraticker` hold.
2. Twenty delisted small-cap tickers chosen at random. Check that each has a price history that ends where the SEC record says trading ended.
3. Ten ARQ rows matched to SEC accessions. Measure how often the match is unique, and whether Sharadar's filing date equals EDGAR's.
4. Total return calculated locally from raw bars and dividends, compared with `closeadj` over the same window.
5. A count of how many US listings in the purchased data are currently in Trading 212's instrument catalogue, without inferring past ISA eligibility.

If the audit fails, cancel before the annual commitment, delete the data within 30 days as the terms require, and record why.

## Decisions for the owner

1. **Research-data budget, separate from the £20 a month invested:** about £31 a month (bundle), about £19 a month (prices only), or £0 (exploratory only, with no profitability claims).
2. **Provider:** Sharadar, bought in the owner's own name, because the personal licence covers only a natural person. Card entry and account creation are for the owner to do.
3. **Scope:** accept a US-only universe for now. UK stays exploratory until a UK source is audited. EODHD is the next candidate, after a written answer from EODHD on LSE delisted coverage.
4. **Licence constraints in a public repository:** accept that no vendor data or derived dataset that could reproduce it is committed, that audit conclusions are published only with Sharadar's approval, and that data is deleted within 30 days of cancellation.
5. **Billing:** one month ($69) for the audit, then annual ($499) only if the audit passes.

## Not settled by this review

- Whether any of these vendors' coverage claims hold. They remain vendor statements until sampled.
- Sharadar's `permaticker`, the meaning of `value` in `actions`, and any accession or CIK field. **Verify on sample.**
- The Norgate licence, Tiingo's delisted coverage and fundamentals price, Massive's history conflict, EODHD's LSE delisted coverage, and CRSP's individual access. None was needed for the recommendation; each stays unverified.
- A versioned US exchange calendar source. This is still open under #5.
