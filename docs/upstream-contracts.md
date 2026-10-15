# Upstream contracts and integration boundaries

Reviewed 15 October 2026. “Upstream shape” here means the external broker and data contracts that Twenty must accommodate. This is a public-documentation/schema audit, not an authenticated account test or a vendor purchase. Existing [data-source due diligence](data-sources.md) still applies.

## Broker findings that change the design

Trading 212 documents beta Invest/Stocks ISA support, separate demo/live environments, primary-account-currency execution, Basic authentication using a key/secret pair, and account-wide endpoint limits. A second key does not provide a second rate budget. [General reference](https://docs.trading212.com/api).

The current OpenAPI inventory exposes account summary, instrument/exchange metadata, positions, orders and account-event history. It does **not advertise a historical OHLCV or general quote-stream endpoint**. Account history cannot supply the research universe's price history. Schema fields distinguish instrument prices from wallet-currency amounts; the overview's currency wording must not override field-level units. [Official schema](https://docs.trading212.com/_bundle/api.json?download=), [positions](https://docs.trading212.com/api/positions).

Order submission takes signed share quantity, not a cash budget. Creation endpoints explicitly warn of duplicate orders on replay. Pending-order retrieval and cancellation are not proof of final execution outcome; reconcile historical orders/fills. The current cash-notional Python simulator is an idealised research reference, not a faithful broker execution adapter. [Orders](https://docs.trading212.com/api/orders), [history](https://docs.trading212.com/api/historical-events).

### Terms need clarification before live integration

The API Help Centre now lists live market, limit, stop and stop-limit orders. However, the linked API Terms §§6.1–6.2 describe testing-only personal use; §6.3 requires prior written consent for applications intended for other end-users, and §6.7 contains prior-consent language for customised interfaces. §7.1 also disclaims real-time market data. This is a concrete mismatch to resolve against the applicable account/entity terms with Trading 212 before live use or offering an integrated broker product to others. A public repository alone does not settle those permissions. No broker enquiry was sent and no agreement was accepted. [Help Centre](https://helpcentre.trading212.com/hc/en-us/articles/14584770928157-Trading-212-API-key), [linked API Terms](https://www.trading212.com/legal-documentation/API-Terms_EN.pdf).

### Relevant published endpoint budgets

Snapshot from the reviewed OpenAPI; refresh before implementation rather than treating these as constants forever.

| Operation under `/api/v0/equity` | Published requests / window |
|---|---|
| GET `account/summary` | 1 / 5 seconds |
| GET `metadata/instruments` | 1 / 50 seconds |
| GET `metadata/exchanges` | 1 / 30 seconds |
| GET `positions` | 1 / second |
| GET `orders`; GET `orders/{id}` | 1 / 5 seconds; 1 / second |
| POST `orders/market`; POST `orders/limit`, `stop`, `stop_limit` | 50 / minute; 1 / 2 seconds each |
| GET `history/orders`, `history/dividends`, `history/transactions` | 20 / minute each |
| POST `history/exports`; GET `history/exports` | 1 / 30 seconds; 1 / minute |

History uses `items` plus `nextPagePath`; the documented maximum page size is 50. Export generation is asynchronous. [Schema](https://docs.trading212.com/_bundle/api.json?download=), [pagination and rate-limit headers](https://docs.trading212.com/api), [history](https://docs.trading212.com/api/historical-events).

## Proposed Twenty boundary (design decisions, not implemented adapters)

Use small C# typed HTTP clients and source-specific DTOs behind domain interfaces. Keep parsing and permissions separate from strategy logic. Introduce a separate worker only when a concrete workload requires it.

| Boundary | Twenty-owned records | Critical rules |
|---|---|---|
| Instrument directory | `SecurityId`, listing/venue/currency/price unit, provider ID mappings, effective intervals, evidence | Broker ticker and ISIN are mapping inputs, not universal permanent listing identities. Never infer historical eligibility from today's catalogue or use `addedOn` as proof of continuous ISA availability. |
| Historical prices | Session date, raw OHLCV, observation basis, corporate-action references, availability, source snapshot | Preserve raw executable prices separately from return-adjusted series. Missing delisting outcomes remain missing. |
| Corporate filings | Company CIK, accession, form, acceptance time, public availability evidence, period, immutable original hash | A period end is not a release time. A company is not interchangeable with one listed security. |
| FX | Base currency, quote currency, quote-units per base, observation/publication time and rate purpose | GBP/GBX scaling and FX direction must be explicit. Reference rates are not broker executable conversions. |
| Broker reads | Environment/account binding, timestamped balances, positions, pending orders, fill/cash events | Read-only first. An account's GBP currency does not establish that it is the user's ISA. Persist raw responses privately, never in public Git. |
| Future order adapter | Local intent ID, cash reservation, signed share quantity, state, broker order/fill IDs | Live capability absent from current web app. Paper ledger first; confirmed legal/account permissions and separate user authorisation before live access. |

Keep cash and share quantities as validated `decimal` values, timestamps as `DateTimeOffset` with source precision retained, and statistical arrays floating point. Absence is not zero. Unknown broker states or ambiguous unit mappings must block new order decisions while preserving evidence for reconciliation.

Do not generate a complete SDK blindly from the beta schema: many reviewed objects lack `required` lists. Explicitly validate the minimal fields needed by Twenty, preserve unknown fields for diagnosis, and test any generated client against captured, sanitised fixtures before using it.

### Quantity execution and ambiguous requests

A future sizing adapter must turn a GBP allocation into a conservative share quantity using verified price units, fresh quote/FX inputs, instrument quantity precision, minimum trade rules and a cost/slippage reserve. Round **down** at the supported step, reserve cash across all pending intents, and keep unspendable residual cash. A market quantity order cannot promise an exact £20 final spend.

Persist the local intent before submission. After a connection timeout, record `SubmissionUnknown`, prevent another submission for that intent, and reconcile pending orders/history/fills. A local idempotency key does not make the remote endpoint idempotent. Do not blindly replay POSTs or treat disappearance from the pending endpoint as a rejected trade. Require explicit resolution if multiple matching broker events make attribution ambiguous.

Use a shared per-account/per-endpoint scheduler; respect returned reset headers and bounded backoff for reads. HTTP clients must constrain pagination to the expected origin/path and keep authentication off arbitrary download hosts. Validate report-download destinations separately. Credentials remain server-side; redact headers and sensitive query parameters from logs, exceptions, tracing and cache keys.

## Filings and FX findings

SEC submissions use zero-padded CIKs and recent parallel arrays; older filing-history chunks are linked separately. Company Facts aggregates entity-wide standard-taxonomy facts across filings, rather than delivering an immutable historical feature table. Normalise by accession and fact context/units; preserve amendments instead of replacing older disclosures. The bulk archives are nightly snapshots, while submission/XBRL updates have different processing schedules. [SEC API documentation](https://www.sec.gov/search-filings/edgar-application-programming-interfaces).

SEC acceptance and public website availability are different events. The SEC describes a typical 1–3 minute website lag that can be longer and is not guaranteed. A fixed three-minute assumption is therefore not a proof of historical availability. Record actual `first_seen_at` prospectively; historical availability needs evidence or a declared conservative policy, tested for sensitivity. Use a declared contact-bearing User-Agent, shared rate limiter below 10 requests/second (initial project choice: two), and bounded caching/retries. [SEC developer FAQ](https://www.sec.gov/about/webmaster-frequently-asked-questions).

ECB reference FX is a useful free research candidate: currencies are quoted against EUR, normally updated around 16:00 CET on working days, and explicitly intended for information rather than transaction pricing. Compute GBP per USD as `(GBP per EUR) / (USD per EUR)` using aligned observations known at the decision time. Retain holiday/missing-data flags and publication assumptions. This does not establish the broker's conversion price. [ECB reference-rate policy](https://www.ecb.europa.eu/stats/policy_and_exchange_rates/euro_reference_exchange_rates/html/index.en.html). The API examples page was unavailable during the initial audit. A [subsequent ECB sample audit](ecb-fx.md) verified CSV/XML delivery, an XML parser and explicit cross-rate units; historical availability and complete FX collection remain unvalidated.

## Market-data candidate implications

EODHD documents daily raw OHLC, split/dividend-adjusted close and split-adjusted volume. Its endpoint's symbol convention differs from Trading 212's. The documented EOD response is a dated price series, not evidence of original vendor delivery time or historical ISA eligibility. Its query-token authentication means a future adapter must scrub URLs from telemetry. Invalid options can silently default, so validate requests and response content types locally. Full rights, coverage, revisions and terminal-return samples still require due diligence; no subscription is selected. [Provider EOD documentation](https://eodhd.com/financial-apis/api-for-historical-data-and-volumes).

Sharadar remains a candidate for US as-reported fundamentals and active/delisted price coverage. As-reported versus restated dimensions and actual release availability must be tested on samples; no paid payload was obtained here. Use the earlier [provider review](data-sources.md), not a claim that vendor labels prove leakage-free data. [Fundamentals documentation](https://sharadar.com/docs/fundamentals).

Indicators should be calculated locally from a versioned dataset with train-only preprocessing. No indicator scraper is needed for momentum, rolling volatility or numeric price features. A permitted document/feed collector is only justified for information absent from the chosen structured sources.

## Acceptance fixtures and unresolved questions

These are required future tests, not tests already executed:

- Missing/wrong currency, GBX scaling, inverted FX, stale quotations, ambiguous listing mappings and unexpected instrument types.
- Partial fills, cancelled orders without a fill, fills arriving after cancellation requests, rejected orders, duplicate pages, conflicting revisions, externally placed trades and stock-split/non-trade events.
- A POST that succeeds upstream but times out locally: no duplicate order after restart or reconciliation failure.
- A new enum value, missing required domain field, 429 response/reset, expired export URL, off-origin pagination path and query-token redaction.
- SEC history chunks, unequal parallel-array lengths, amendment/duplicate accession handling, comparable-period facts disclosed later, acceptance versus first-public timing, and schema/parser changes.
- Raw versus adjusted price changes after a corporate action; missing terminal outcomes; FX holidays and late publication.

Resolve before execution: applicable broker terms/consent, verified ISA account binding, instrument-specific quantity step/minimum/currency rules, price freshness, fill/FX semantics, and attainable data licence/coverage. Before collection, supply a genuine monitored SEC contact identity without committing private contact details. These are gates in issues #3, #4, #5, #6 and #10; they do not prevent building the offline contracts and fixtures.
