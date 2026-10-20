# Market-data selection for issue #5 (free sources only)

Reviewed 16 October 2026. **Constraints set by the owner on 16 October 2026:** Twenty uses free data only, with no paid data tier, trial or card on file for data. The owner also accepts the risk of scraping against a site's terms. Terms and robots.txt are quoted below as information, not as a filter. The remaining limits are in the [scraping policy](data-sources.md#scraping-policy-and-failure-handling): public unauthenticated pages only, no defeating logins, paywalls, CAPTCHAs or bot challenges, no IP rotation, slow and backed-off collection, and no scraped data in the public repository or image. A source behind a bot challenge is **unavailable**.

The question that matters for each source is whether it **keeps the price history of delisted tickers**. That is the survivorship gap. Scraping a source that drops delisted tickers gives a survivor-only panel for the past. It is useful only for capturing prices *forward*, before tickers disappear.

This is a review of providers' own documentation, terms and robots.txt files, plus a few documented public samples:

- Tiingo's public `supported_tickers.zip` (SHA-256 `1493c67e…4e65`).
- Alpha Vantage's documented `demo` request for delisted symbols as of 2014-07-10 (SHA-256 `ced11649…3900`).
- The ClinicalTrials.gov API version and OpenAPI files.
- Nasdaq Trader's symbol-directory header.

Nothing was bought, no account was opened and no key was requested. Samples were kept in a scratch directory, not in Git. An SEC full-index request without a contact-bearing User-Agent returned `403`, so EDGAR delisting counts below are not measured. **Unverified** means the documentation does not settle the point.

The paid options reviewed earlier (Sharadar, EODHD, Norgate, Tiingo Power, Massive, CRSP) are summarised at the end for the record. None is recommended under this constraint.

## Free sources

### Group 1: no account needed

| Source | What it provides | Delisted and survivorship | Terms and robots.txt | Fit |
|---|---|---|---|---|
| **SEC EDGAR Financial Statement Data Sets** ([page](https://www.sec.gov/data-research/sec-markets-data/financial-statement-data-sets), [field definitions](https://www.sec.gov/files/aqfsn_1.pdf)) | Face-financials numbers from XBRL filings, quarterly files from 2009 Q1. Each submission has `adsh` (accession), `filed`, and `accepted` "date and time"; "Filings accepted after 5:30pm EST are considered filed on the following business day". `prevrpt` marks a later amendment. Values are "as filed" | Covers every XBRL filer, including companies that later delisted. Coverage of smaller companies begins later because XBRL was phased in (**unverified** start date per company size) | Public. SEC: "Current max request rate: 10 requests/second", declare a User-Agent with a contact, and "The SEC does not allow botnets or automated tools to crawl the site" outside its policy ([accessing EDGAR data](https://www.sec.gov/search-filings/edgar-search-assistance/accessing-edgar-data)). Bulk files are the documented route | **Primary fundamentals source.** Point-in-time by accession and acceptance time, which contract v1 requires |
| **SEC EDGAR filings and indexes**, including Form 25 | Indexes "from 1994Q3 through the present". Form 25 is the notice of removal from listing; removal "shall be effective 10 days after filing the Form 25" ([Form 25](https://www.sec.gov/about/forms/form25.pdf)). Merger proxies and 8-Ks give deal terms | Delisting **events** for exchange-listed securities, with dates. No prices and no proceeds | As above. Requires a genuine monitored contact in the User-Agent, which the owner must supply ([upstream contracts](upstream-contracts.md)) | Delisting-event spine and the denominator for coverage audits. The source for resolving terminal proceeds by hand |
| **Kenneth French Data Library** ([library](https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/data_library.html)) | Factor returns and portfolio returns (size, value, profitability, investment, momentum, reversal, industries, accruals, issuance, variance), daily, weekly and monthly. US data from CRSP; monthly returns are "compounded daily returns with dividends reinvested on their ex-dates" | Built from CRSP, so delisted firms are in the portfolios. **Portfolio-level only**: no individual stock returns | "Copyright Eugene F. Fama and Kenneth R. French"; no other usage terms stated. robots.txt does not disallow the library path. Full history is recalculated at each update, so vintages must be archived | **Credible for Family C** (market and factor-level rules) and as a benchmark |
| **Shiller data** ([shillerdata.com](https://shillerdata.com/)) | Monthly S&P prices, dividends, earnings, CPI and CAPE from January 1871, plus a total-return version. "Prices are monthly averages of daily closing prices" | Index-level, so no survivorship problem at the stock level | No licence stated; the disclaimer says the user "assumes the entire risk". robots.txt disallows only `/404` | Long-horizon checks for Family C. Monthly *averages* blur trend signals, so use French daily or monthly market returns for the rules themselves |
| **Nasdaq Trader symbol directory** ([definitions](https://www.nasdaqtrader.com/trader.aspx?id=symboldirdefs)) | `nasdaqlisted.txt` and `otherlisted.txt`, "updated periodically through-out each day", with a `File Creation Time` trailer | **Current listings only.** Past versions not documented | No robots.txt (404). The disclaimer page also returns 404, so terms are **unverified** | Daily forward snapshots of the full US listed universe, which are survivorship-free by construction from the first day collected |
| **Tiingo `supported_tickers.zip`** ([overview](https://www.tiingo.com/documentation/general/overview)) | Public list, "updated daily", with ticker, exchange, asset type, currency, `startDate`, `endDate` | Contains ended tickers: 8,175 NYSE, Nasdaq or AMEX stocks with an `endDate` before September 2026 in the sample. See the coverage bound below | Overview: "data is for internal and personal use only" and "You may not redistribute the data in any form" | Cross-check of delisting dates and price-history spans |
| **ECB reference FX** | Already implemented ([ECB FX](ecb-fx.md)) | n/a | Public | USD→GBP reporting conversion |
| **ClinicalTrials.gov API v2** | Live API (version 2.0.5, data timestamp 2026-10-15). Its OpenAPI says it provides "the most recent version of the clinical trials" | n/a. **No record history through the API**, so a field edited after an outcome leaks it | robots.txt has `Disallow: /api/` for all user agents, while the API is documented for programmatic use. Terms page not readable without JavaScript (**unverified**). Under the owner's decision, use the documented API | Family B, collected forward |
| **Stooq** | Bulk historical daily files and per-symbol pages. Markets, including UK, **unverified** | **Unknown.** Stooq documents nothing about delisted tickers; it must be sampled with known delistings before relying on it | robots.txt: `User-agent: *` / `Disallow: /`; only Googlebot and Bingbot are allowed. **Automated collection is prohibited**; the owner accepts this risk. Whether bulk downloads sit behind a CAPTCHA is **unverified**. If they do, Stooq is unavailable | Candidate for current US and UK daily prices captured forward, and for past history if sampling shows it keeps delisted tickers |
| **Yahoo Finance** (including libraries such as `yfinance` that call it) | Daily OHLC, dividends and splits for currently listed symbols, US and LSE | **No.** A maintainer of the `yahoo-finance2` library: when a stock is delisted, "ALL DATA will no longer be available", including periods when it was listed ([library docs](https://github.com/gadicc/node-yahoo-finance2/blob/devel/docs/modules/historical.md)). Not verified by Twenty | [Terms](https://legal.yahoo.com/us/en/yahoo/terms/otos/index.html) §2(d): you may not "access or collect data … using any automated means", including "scrapers", nor build "any database, archive … data feed". robots.txt also blocks `anthropic-ai` and `ClaudeBot` entirely. **Automated collection is prohibited**; the owner accepts this risk. Consent pages or bot challenges from a datacentre IP make it unavailable | **Forward capture only.** Cannot fill the past gap. A daily capture of every listed US and UK symbol keeps prices for tickers that delist later. The only free route to UK prices found so far |

### Group 2: free account or API key needed (owner decision 2)

| Source | What the free tier gives | Delisted | Terms | Fit |
|---|---|---|---|---|
| **Tiingo Starter** ([pricing](https://www.tiingo.com/about/pricing), [EOD docs](https://www.tiingo.com/documentation/end-of-day)) | 500 unique symbols a month, 1,000 requests a day, 50 an hour, 1 GB a month; "30+ Years" of history. Raw and adjusted OHLC, `divCash`, `splitFactor` | Ended tickers appear in the public list. Authenticated BBBYQ history was retrieved on 18 October; broader delisted coverage and terminal proceeds remain unverified | "internal and personal use only"; no redistribution | The only free source of individual US daily prices found. 500 symbols a month means a full backfill of about 16,300 US exchange-listed tickers takes about 33 months |
| **Alpha Vantage** free key ([docs](https://www.alphavantage.co/documentation/), [support](https://www.alphavantage.co/support/), [terms](https://www.alphavantage.co/terms_of_service/)) | 25 requests a day. Daily prices give only the latest 100 points: "The "full" outputsize is available to premium keys"; adjusted daily is "a premium API function". `LISTING_STATUS` returns active or delisted US stocks and ETFs on any date after 2010-01-01 | `LISTING_STATUS` exists, but the documented demo for 2014-07-10 returned only 430 delisted symbols, of which 61 delisted in 2014 and 145 in 2013 | §2: "personal, non-commercial use"; use beyond "investment analysis, research, testing, monitoring" and other private activity counts as commercial | **Low value.** No free price history. `LISTING_STATUS` is at most a cross-check |
| **FRED and ALFRED** ([terms](https://fred.stlouisfed.org/docs/api/terms_of_use.html)) | Macro series and vintages | n/a | A key is required. Some series are third-party copyright, "contain the word 'Copyright' in their notes", and need the owner's permission for non-personal use | Optional later features (Family C) |
| **openFDA** ([authentication](https://open.fda.gov/apis/authentication/), [terms](https://open.fda.gov/terms/)) | Works without a key at 240 requests a minute and 1,000 a day per IP; with a key, 120,000 a day. Drugs@FDA approvals | n/a | "Unless otherwise noted … public domain" | Family B. No key is needed at Twenty's volume, so effectively Group 1 |
| EODHD free | 20 calls a day, 1 year of history ([pricing](https://eodhd.com/pricing)) | n/a | Personal use | Not useful |

## What free data cannot do

### No free source of individual delisted-stock prices before about 2015

Tiingo's public ticker list is the only free evidence of per-stock price spans that include delistings. Counting US exchange-listed stocks with an `endDate` by year:

| End year | 2009 | 2010 | 2011 | 2012 | 2013 | 2014 | 2015 | 2016 | 2017–2025 |
|---|---|---|---|---|---|---|---|---|---|
| Stocks | 41 | 45 | 77 | 55 | 142 | 151 | 298 | 427 | 433–1,166 a year |

Fewer than 10 tickers end before 2009. The jump from about 50–150 a year to 300–1,100 a year from 2015 is a change in Tiingo's coverage, not in the real delisting rate. The inference is that **most US delistings before about 2015 are absent**, so any free per-stock panel before then is survivorship-biased. The exact coverage ratio needs the EDGAR Form 25 count for each year as the denominator. That is the first audit once the owner supplies an SEC contact. Alpha Vantage's 430 delisted symbols for 2009–2014 point the same way.

Even after 2015, Tiingo's free tier is rationed (about 33 months to backfill), and whether it serves delisted tickers' prices is unverified.

### No delisting proceeds anywhere

No free source gives what holders received. Every delisting starts as `TerminalOutcome = Missing`. It may be resolved only from SEC deal filings (cash or stock terms) or a confirmed liquidation.

### No UK delisted history

No free UK source that keeps delisted tickers was found. Yahoo drops them. Stooq is unknown until sampled. UK prices can be captured **forward** from Yahoo or Stooq, so a UK universe becomes survivorship-free only from the first day of collection. **UK backtests are out of scope**. UK forward collection is in scope.

### Bounding the bias that remains

When a stock's price history ends without a known outcome, report every result under three terminal assumptions and do not claim a result that changes sign or ranking between them:

1. **Last price**, which assumes holders got the last quote. This is the optimistic bound.
2. **Literature substitute** for performance-related delistings: −55% for Nasdaq stocks, which "corrects the bias" in CRSP's Nasdaq data, and which also made the Nasdaq size effect disappear ([Shumway and Warther 1999](https://ideas.repec.org/a/bla/jfinan/v54y1999i6p2361-2379.html)). −30% is the convention for NYSE and AMEX, attributed to [Shumway (1997)](https://ideas.repec.org/a/bla/jfinan/v52y1997i1p327-40.html) by secondary sources such as [SAFE's CRSP data description](https://datacenter.safe-frankfurt.de/documents/Data_Description_CRSP_Shumway_20250317.pdf); the exact figure is not verified in the 1997 paper itself.
3. **−100%**, the pessimistic bound.

A stock missing from the panel altogether cannot be bounded this way. Report the panel's coverage ratio against EDGAR Form 25 events for each formation year. Below about 90% (a project threshold, not a literature figure), mark that year's results exploratory.

## What is credible for each family

| Family | Credible backtest now | Label exploratory | Wait for forward collection |
|---|---|---|---|
| **C: exposure rules** | Long-only trend and volatility-scaling rules on the market and factor portfolios in the French library (CRSP-based, dividends reinvested, from the 1920s for US market series (**verify** per file)). Shiller's series as a long-horizon cross-check. Costs and tracking error of a real ETF must be modelled separately, because a portfolio return is not a fill | Per-position stops inside a stock-level momentum strategy (P38), which needs individual prices. French's momentum *portfolios* can be tested only at portfolio level | Execution slippage of an actual ETF in the ISA |
| **B: biotech catalysts** | FDA approval dates and outcomes from openFDA Drugs@FDA as historical facts. Phase-transition base rates only where a dated snapshot of each trial record exists | Any outcome classifier trained on today's ClinicalTrials.gov records, because the API serves only the latest version and later edits can leak outcomes. Event studies of announcement returns, since small biotechs are where delisted prices are missing | **Daily ClinicalTrials.gov snapshots from now.** Model-versus-price tests on catalysts that happen after collection starts |
| **A: multibagger and wipeout** | **Nothing.** No free per-stock survivorship-free price history covers enough formation dates. A 2015 start gives no complete 10-year label until 2025 and about seven 5-year formation dates | A census of 10x and wipeout outcomes from 2015, using Tiingo prices (if a free key is approved), EDGAR fundamentals by accession, Form 25 delisting events and the three-bound reporting above. A descriptive census only, with no profitability claim | **The real test.** Daily listing snapshots (Nasdaq Trader + EDGAR) and as-filed fundamentals from now, with paper predictions scored as outcomes arrive. 5-year labels first resolve in 2031 |

This changes the growth plan's order: Family C first, then the forward collectors for A and B, then descriptive work. No Family A model result should be presented as evidence of an edge.

## Ingestion contract changes needed against data contract v1

1. **A portfolio or index return series kind.** v1 can store prices only for a `Listing`. French portfolios and Shiller series are not listings and must not be disguised as them. Proposed: add a `returnSeries` observation (series ID, source series name, period, frequency, return definition such as "dividends reinvested on ex-date", value) through a reviewed contract change. Archive each French file **vintage**, because the library recalculates full history at each update.
2. **Listings from daily snapshots.** Each Nasdaq Trader file becomes a `SourceSnapshot` with `FirstSeenAt` = retrieval time. A symbol appearing opens a `Listing` interval with `ObservedPublication` evidence. A disappearance closes it, but only after confirmation from a Form 25 or two consecutive absent snapshots, to avoid closing on a bad file. `Test Issue = Y` rows are dropped.
3. **Fundamentals from the Financial Statement Data Sets.** `Filing` from SUB (`adsh` → accession, `accepted` → `AcceptedAt`). `FundamentalFact` from NUM keyed by accession, with `prevrpt` used only to link revisions, never to overwrite. Apply the "after 5:30pm" rule only to the `filed` date. Use the accepted timestamp, plus the existing SEC availability policy, for `AvailableAt`.
4. **Delistings from Form 25.** `CorporateAction(Delisting)` with `EffectiveAt` = filing + 10 days, unless an amendment says otherwise, and `TerminalOutcome = Missing`. Resolve to `Cash`, a successor listing or `Zero` only from a cited filing. Mapping a Form 25 (company CIK and class of security) to a `Listing` needs a CIK-to-ticker history. EDGAR's current ticker files do not give one, so the mapping must be built from snapshots going forward and from filing text going back. If no single listing matches, fail closed.
5. **Prices from Tiingo (if approved).** Store a `Raw` bar from `open/high/low/close/volume` and `CorporateAction` dividends and splits from `divCash` and `splitFactor`. Do **not** turn `adjClose` into a `TotalReturnAdjusted` bar: v1 forbids it. Calculate returns locally and use `adjClose` as a cross-check. `ConservativePolicy` availability (next session open) for the backfill; `ObservedPublication` going forward. Tiingo puts its token in a header or query string; it must be redacted from URLs and logs either way. *(Superseded: the reviewed `listing-prices-v2` contract does store a `TotalReturnAdjusted` bar from the adjusted fields, carrying split-adjusted `adjVolume`; see [price conversion](listing-prices.md).)*
6. **Clinical trials.** Each daily ClinicalTrials.gov pull is an immutable snapshot. A study's field change becomes a new revision available from that snapshot's `FirstSeenAt`. A new observation kind (trial record) is needed. It is out of v1's scope and belongs with Family B's own contract.

## Collector hosting

Collectors for sources whose terms forbid automation (Yahoo and Stooq, and any later scraped site) run on **one disposable VPS**, proposed as Oracle Cloud Always Free, not on Quark's home IP. Collectors for public-sector APIs (SEC, ClinicalTrials.gov, openFDA, ECB, French, Shiller) can stay on Quark.

What Oracle documents ([Always Free resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm)):

- Up to two `VM.Standard.E2.1.Micro` instances (1/8 OCPU, 1 GB), or Ampere A1 equivalent to 2 OCPUs and 12 GB. 200 GB block storage, 20 GB object storage, 10 TB a month outbound.
- **Reclamation:** "Idle Always Free compute instances may be reclaimed by Oracle". An instance counts as idle if, over 7 days, 95th-percentile CPU is below 20%, network below 20%, and (A1 only) memory below 20%. A slow collector will meet that definition, so **expect reclamation**. Do not pad utilisation to avoid it; rebuild instead.
- **Card verification:** Oracle's [sign-up guide](https://docs.cloud.oracle.com/iaas/Content/GSG/Tasks/signingup_topic-Sign_Up_for_Free_Oracle_Cloud_Promotion.htm) has the user add a credit card for verification. It says there may be a small temporary charge and that the card is not charged unless the account is upgraded. Only the owner can do this.

Design:

1. **Stateless and rebuildable.** A cloud-init script and a pinned container image recreate the collector from scratch. The schedule, source list and pacing live in the repository; the API keys and the tunnel key are provisioned by the owner, never committed.
2. **The VPS never holds the only copy.** Each capture is written as an immutable, timestamped raw object with its SHA-256. It is pushed to a drop directory in Quark's private store over an authenticated tunnel (WireGuard, or SSH with a key that can only write to that directory). Quark recomputes the hash, moves the object into its immutable store and acknowledges. The VPS deletes its local copy only after that acknowledgement. If the VPS disappears, at most the unacknowledged captures are lost, and a gap is recorded rather than backfilled from guesses.
3. **Slow and backed off.** One request at a time per source, pacing far below any published limit, conditional requests and caching. Exponential back-off that **stops** on 403/429 or a challenge page and raises an alert. No retries from another IP.
4. **Challenges mean unavailable.** If a source starts serving a CAPTCHA, Cloudflare or other bot challenge, or a consent wall that needs a login, the collector marks the source unavailable and records the date. There is no challenge-solving (Byparr, FlareSolverr or similar) and no proxy or IP rotation. Datacentre IPs are challenged more often than home IPs, so Yahoo may be unavailable from the VPS. That is an accepted outcome, not a problem to engineer around.
5. **Nothing reaches public artefacts.** Neither the VPS nor Quark publishes captures, and the repository and published image contain only collector code and synthetic fixtures.

## Ingestion order

1. **French library and Shiller → `returnSeries`** (needs contract change 1). This unblocks Family C entirely, with no account and no contact needed.
2. **Daily forward listing snapshots** from Nasdaq Trader and Tiingo's public ticker list. Cheap, and every day not collected is lost survivorship-free history. Start as soon as possible.
3. **SEC Financial Statement Data Sets and Form 25 events** through the existing SEC collector. Blocked only on the owner supplying a monitored SEC contact for the User-Agent. Then run the Form 25 coverage audit against Tiingo's `endDate` counts.
4. **ClinicalTrials.gov daily snapshots and openFDA Drugs@FDA.**
5. **Daily forward price capture** for every listed US symbol (from step 2's snapshots) and for UK symbols, from Yahoo or Stooq, on the VPS collector. Prices for tickers that delist later are kept from the first capture onward. Sample Stooq first with ten known US and UK delistings. If it keeps their history, it also becomes a past-history source.
6. **Tiingo free-key price backfill from 2015**, if the owner approves a free account. Ration 500 symbols a month, prioritising delisted and small-cap tickers flagged by Form 25, because that is where survivorship bias concentrates.
7. **FRED/ALFRED** and **Alpha Vantage `LISTING_STATUS`**: optional, only when a feature or cross-check needs them.

## Decisions for the owner

1. **Accept the limits.** Family A cannot be backtested credibly on free data. It becomes a 2015+ descriptive census plus forward paper predictions, with first 5-year outcomes in 2031. Family C is credible now at portfolio level. UK work is forward collection only. Scraping more sites does not change this: the sites found so far drop delisted tickers or are unverified.
2. **Free accounts (Group 2).** Approve or refuse a free Tiingo key, the only free source of individual prices, and optionally a FRED key. The owner creates any account. Twenty never creates accounts or enters credentials.
3. **SEC contact.** Supply a genuine monitored contact for the SEC User-Agent, kept out of Git. Without it, EDGAR returns `403`, as it did during this review.
4. **VPS account.** Oracle Cloud sign-up requires card verification even for Always Free resources (see [collector hosting](#collector-hosting)). Only the owner can do this, and it is the one place a card is involved.
5. **Contract change.** Approve adding `returnSeries` (and later a trial-record kind) to the data contract.

## Paid options reviewed and set aside

Recorded so the work is not repeated. Prices from the providers' pages on 16 October 2026; full citations are in this file's history at commit `e1f28ff`. **Sharadar** (bundle $499/year: delisted US prices from 1997, as-reported fundamentals, a personal licence that permits automated trading of your own account) was the strongest candidate. **EODHD** ($999.90/year) claims LSE history, with UK delisted coverage unverified. **Norgate** ($630/year) has no UK coverage and no historical fundamentals. **Tiingo Power** costs $300/year. **Massive** (formerly Polygon) costs $199/month. **CRSP** is for academic licensees only. If the owner's constraint ever changes, Sharadar's one-month audit is the first thing to revisit.


## Authenticated follow-up · 18 October 2026

The owner supplied a Tiingo Starter key, stored privately and configured through Dockhand encrypted secrets. Active AAPL and delisted BBBYQ daily histories were retrieved successfully under bounded explicit requests. This removes the initial account dependency and the unknown for that one delisted sample; it does not establish complete historical coverage or terminal proceeds. See [implementation, free-tier limits and research-only portfolio conversion](real-data-milestone.md).
