# Data sourcing and collection design

Reviewed 15 October 2026. This is a design and provider-documentation review. No commercial feed was purchased or completeness-certified. Research-data spending is distinct from the £20 monthly investment budget; assuming a separate budget without approval would conceal the economics.

The [ECB sample audit](ecb-fx.md) now verifies reference-FX wire shape and conversion units against retrieved samples; historical publication/vintage evidence and full market coverage remain unresolved.

See [upstream contracts](upstream-contracts.md) for the current schema audit, cash-notional versus quantity-order gap, broker terms discrepancy and acceptance fixtures.

## Recommended source order

| Need | Candidate | What the documentation supports | Twenty decision |
|---|---|---|---|
| US disclosures and fundamentals | [SEC public EDGAR APIs](https://www.sec.gov/search-filings/edgar-application-programming-interfaces) | Submission histories, XBRL facts, public JSON access and bulk archives | First collection target; archive original filings and accession-linked facts |
| Signal definitions and comparison portfolios | [Open Source Asset Pricing](https://www.openassetpricing.com/data/) | Documented predictor implementations, portfolio returns and stock-level signals | Research reference; not a complete tradable stock-price feed |
| US historical stocks/fundamentals | [Sharadar fundamentals](https://sharadar.com/docs/fundamentals), [equity prices](https://data.nasdaq.com/databases/SEP/documentation?anchor=sample-data) | Active/delisted coverage and separate as-reported versus most-recent reporting dimensions | Candidate for due diligence if a research-data budget is approved; validate end-of-life and timing semantics |
| Broader exchange prices and delisted symbols | [EODHD delisted-data documentation](https://eodhd.com/financial-apis/delisted-stock-companies-data-2) | Listing delisted symbols and querying their historical prices, splits and dividends | Alternative candidate; delisted coverage alone does not prove correct terminal returns or point-in-time fundamentals |
| UK corporate announcements | Company investor-relations feeds; [LSEG RNS](https://www.lseg.com/content/dam/lseg/en_us/documents/rns/rns-pricing-and-policy-guidelines-2026.pdf) | RNS has formal feed and usage policies | Prefer permitted feeds or licensed access; do not presume public display permits unrestricted collection/redistribution |
| Historical macro releases | [ALFRED](https://fred.stlouisfed.org/docs/api/fred/alfred.html) | Vintage versions of economic observations | Optional later feature source; use vintages known at the decision time |
| Broker account/execution | [Trading 212 API](https://docs.trading212.com/api) | Account, instrument, order and history integration | Separate adapter; account history is not a research price dataset |

Sharadar explicitly distinguishes as-reported ARQ/ARY/ART observations from restated MRQ/MRY/MRT views. Use the former for historical features, with delivery and execution lags; a fiscal quarter-end is not a disclosure time. Vendor coverage claims remain vendor claims until sampled and audited.

The Open Source Asset Pricing site currently identifies its October 2025 release, with most data through December 2024 and some option-related inputs ending earlier. Release fixes and uneven coverage mean that an unversioned download is unsuitable for reproducible comparisons. Its stock-level signals omit some CRSP-derived fields; do not assume public signals include all underlying returns or licences.

## Price-data acceptance

Before selecting a vendor, obtain a sample and written documentation sufficient to answer:

1. Are inactive securities, listing dates, mergers, bankruptcies, ticker changes and terminal/delisting outcomes included? Does a disappearing quote become a cash distribution, a loss, an OTC continuation or missing data?
2. Are raw executable prices, splits, dividends, corporate-action dates and adjustment factors separately available? Is the adjusted series a total-return or split-only series? Gross or net of withholding?
3. Can the universe be constructed using only contemporaneous information? Today's index members and current broker catalogue are not historical membership records.
4. Are observation, publication, vendor availability and revision timestamps distinguishable? Can historical fundamentals be joined by accession or as-reported version?
5. What are the permitted personal research uses, storage/retention after cancellation, derived-data rights, rate limits and redistribution restrictions?
6. What is the full annual research cost, including the required coverage tier? Is it compatible with a separate agreed research budget?

Audit examples must include a delisting, merger, dividend, split, restatement, reporting delay and symbol change; compare with primary announcements. Benchmark prices need the same currency/distribution conventions. Cross-check sampled returns, not merely closing-price agreement on one famous surviving company.

**No primary full-market price provider is selected yet.** The owner has since ruled out paid data; the free-sources selection and its limits are in [market-data selection](market-data-selection.md). Public documentation narrows the candidates; it cannot settle contractual rights, completeness or the cost of the exact package. Until then, fixed-survivor or free exploratory datasets must be labelled exploratory and cannot support a claim of strategy profitability.

## Collection contract

Store each original item with:

```text
source_id, source_url, instrument_id/company_id, accession_or_event_id
observation_time, published_at, available_at, first_seen_at, retrieved_at
content_sha256, original_content_type, revision_of, parser_version
licence_reference, timezone, timing_precision, data_quality_flags
```

Use immutable original objects plus versioned parsed tables. `first_seen_at` measures our own collection and must not be invented for old filings. If historical availability is only a date, declare that precision and apply a conservative next-session rule. Never replace a statement with a restatement in place. Joins must use information available before the decision, not the statement's economic reporting period.

SEC data should be linked by company CIK and accession, not ticker alone. Use public submission metadata to recover acceptance/publication timing and retain document identity. Company Facts is a retrieval convenience; a latest fact or calendar frame is not automatically a clean historical snapshot. Amendments and later comparative-period disclosures need separate versions.

Use a declared contact-bearing User-Agent and a shared request limiter below the SEC ceiling of 10 requests/second, with caching and backoff. The proposed initial limit is two requests/second, a project choice rather than an SEC requirement. Bulk archives are appropriate for larger backfills. [SEC access guidance](https://www.sec.gov/about/webmaster-frequently-asked-questions).

## Scraping policy and failure handling

Use APIs/feeds first. Scraping is justified only for a permitted source with relevant information unavailable in a structured form. Respect access policies, rate limits and licences; do not bypass authentication, paywalls or access controls. Prefer company filings and announcements over social-media sentiment for the initial experiment.

Collectors need retry limits, source-specific pacing, idempotent event keys, duplicate/revision detection, schema-change checks and a quarantine queue. Missing or stale critical data should prevent a new signal rather than silently becoming zero or an optimistic forecast. External document text remains untrusted data and must never execute instructions or acquire broker credentials.

For filing comparisons, align the same report type and comparable fiscal period, isolate sections and tables, preserve added/deleted passages and distinguish templates from substantive changes. Save parser confidence and evidence spans; require human-reviewed examples before interpreting model classifications as financial facts.

## GBP and instrument identity

Choose a durable security identifier and retain ticker/exchange histories plus broker identifiers. Keep share-price units explicit: GBX/pence versus GBP is a factor-of-100 issue. Verify current ISA availability separately from the research universe; historical Trading 212 availability is not established by today's catalogue.

For a foreign asset, if `FX_t` is **GBP per unit of foreign currency**, a consistently defined local total return converts as:

```text
1 + return_GBP(t,t+1) = (1 + return_local(t,t+1)) * FX_(t+1) / FX_t
```

Do not invert the FX quote silently or count a conversion twice. Adjusted return indices are suitable for return attribution, not for reconstructing share quantities, bid/ask quotes or broker order fills. A later execution simulator needs raw quotes and corporate-action handling separately.

## Account costs and economic constraints

Trading 212 documents live API support for the Stocks & Shares ISA and a beta interface with primary-currency execution. Its fee schedule lists a 0.15% FX charge and £1 minimum stock/ETF orders. Those facts do not establish successful execution for every instrument or account permission. The current API Terms explicitly restrict Algorithmic Trading in §4.2(a), beyond the testing/live discrepancy in the first review. The user instructed development to continue without seeking clarification; this project has not accepted terms or represented consent. See the [researched setup instructions](trading212-setup.md) and [linked API Terms](https://www.trading212.com/legal-documentation/API-Terms_EN.pdf). [Account/API support](https://helpcentre.trading212.com/hc/en-us/articles/14584770928157-Trading-212-API-key), [ISA fees](https://www.trading212.com/terms/isa).

Apply FX, spread, slippage and purchase taxes per instrument/side. Many UK company-share purchases incur 0.5% SDRT; exemptions and fund treatment require actual instrument classification. An ISA shields qualifying UK investment income/gains; it is not a blanket transaction-tax exemption. [HMRC share taxes](https://www.gov.uk/tax-buy-shares), [ISA rules](https://www.gov.uk/individual-savings-accounts/how-isas-work).

A £5 monthly research subscription is £60 a year, equal to 25% of £240 annual contributions. Report both investment returns and the total cash cost of the research programme. Free sources and existing hardware are preferred initially, while acknowledging that reliable whole-market historical data may require paid access.
