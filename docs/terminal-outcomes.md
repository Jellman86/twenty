# Terminal-outcome evidence and captured-history audit

Reviewed 20 October 2026, advancing [historical-data acceptance #5](https://github.com/Jellman86/twenty/issues/5). The offline `terminal-history-audit` command compares captured history dates with manually reviewed issuer/exchange assertions. It preserves request-window limitations, unsupported consideration and unresolved dates in the result. It does not import actions, repair histories or certify proceeds.

```sh
dotnet run --project src/Twenty.Data -c Release -- terminal-history-audit \
  --archive <private-offline-capture-copy> \
  --reference docs/terminal-outcome-references.json \
  --sources <private-source-document-directory>
```

The bounded `terminal-reference.v1` bundle declares up to 50 symbols, sources, event kinds/dates, optional explicitly reviewed last-trading/suspension dates, and consideration components. Completed mergers and subsequent listing notices have different semantics. Completion does not supply an unstated last trading date; suspension is distinct again. Common-share cash entitlements are not actual payment dates or account receipts. A cash-plus-stock merger and cash-plus-CVR merger remain unsupported even when their date comparison passes.

Each source is a private `<contentSha256>.bin` file. `original-response` means saved response bytes; `web-tool-extraction` means the saved web-tool extraction envelope, not original HTML. Direct issuer/SEC downloads returned HTTP 403 for the Ellington pages and ACC filing; their primary-source web extractions are retained explicitly as extractions. The audit checks hashes, representation labels, source references, bounded shapes and capture identity/size/content integrity. Hashes bind the reviewed material; they cannot authenticate a site, validate the manual transcription or independently recover a historical publication vintage. Updated Nasdaq notices retain earlier headline dates; those dates must not be used as availability times for later completion updates.

The latest captured history per symbol is selected, with same-instant disagreements in payload, URL or size rejected. Identical retransmissions select the first ordinal capture id. The original Tiingo URL must identify the symbol and an unambiguous bounded start/end request. The audit distinguishes requests that do not reach the event or final session from missing terminal bars within a covering request. Rows outside the request/capture date, missing reviewed final sessions and rows after the stated last trading date are findings. Post-trading date totals are uncapped; the date list is capped at 100. Unverified last trading dates and nonterminal subjects cannot yield a passing audit. A clean comparison still does not establish session completeness, adjustment correctness, payout timing, taxes, FX or point-in-time investability.

## Fixed six-symbol sample

This is the complete first-six sample declared on 19 October in the [listing audit](listing-audit.md), without additional Tiingo requests or symbol substitutions: AAIC, AAWW, ABMD, ABST, ACB, ACC. Read-only copies of the six existing history observations and hash-verified payloads were audited offline. The original requests began on 2010-01-01 and ended at the sampled producer segment's end plus 31 days. [Normalized source assertions](terminal-outcome-references.json) and [compact audit evidence](terminal-outcome-audit.json) are committed; raw responses, extractions, histories and full generated output remain private.

| Symbol | Primary evidence | Captured-history finding |
| --- | --- | --- |
| AAIC | Ellington completed the merger on 2023-12-14; last trading was 2023-12-13. Common shares entitled to 0.3619 EFC shares plus USD 0.09, with cash in lieu of fractional shares. [Issuer completion](https://www.ellingtonfinancial.com/news-releases/news-release-details/ellington-financial-completes-merger-arlington-asset-investment), [issuer FAQ](https://ir.ellingtonfinancial.com/arlington-asset-investment-corp-merger-faqs-0). | 3,512 rows through 2023-12-14, including one row after the stated last trading date. Mixed consideration unsupported. |
| AAWW | Nasdaq states completion and last trading on 2023-03-17, suspension on 2023-03-20, USD 102.50 cash per common share. [Nasdaq ECA 2023-160](https://www.nasdaqtrader.com/TraderNews.aspx?id=eca2023-160). | 3,325 rows: the tail jumps from 2023-03-17 to 2023-04-03. Exactly one row is after stated last trading; this is not evidence of continuous trading through April. |
| ABMD | Last trading 2022-12-21; merger completed before business opened on 2022-12-22, when the stock remained halted; suspension 2022-12-23. USD 380 cash **plus one non-transferable CVR** per common share. [Nasdaq ECA 2022-460](https://www.nasdaqtrader.com/TraderNews.aspx?id=eca2022-460). | 3,268 rows, including 2022-12-22 and 2023-01-03 after last trading. The CVR must not be ignored, assigned zero or replaced by an assumed maximum payment. |
| ABST | Crosspoint Capital acquisition completed before market open 2023-07-27 for USD 11.50 per common share. Nasdaq states last trading 2023-07-26, halted on completion day and suspension 2023-07-28. [Issuer completion](https://www.absolute.com/press-releases/absolute-software-announces-completion-of-acquisition-by-crosspoint-capital-partners), [Nasdaq ECA 2023-425](https://www.nasdaqtrader.com/TraderNews.aspx?id=ECA2023-425). | Request ended **2020-11-28**; its 22 rows span 2020-10-28 to 2020-11-27. This request cannot test the 2023 terminal period or reviewed last session. Earlier-segment coverage remains unresolved. |
| ACB | Nasdaq's 2024-02-15 notice announces a 1-for-10 reverse split effective 2024-02-20 under ACB. It is a later listing/action notice, not a terminal event. [Nasdaq ECA 2024-70](https://www.nasdaqtrader.com/TraderNews.aspx?id=ECA2024-70). | Request ended 2021-06-26; 1,753 rows end 2021-06-25. The window cannot establish later history, and its endpoint cannot establish delisting. |
| ACC | Merger completed 2022-08-09; common shares converted into a USD 65.47 cash entitlement subject to stated exclusions/withholding. The filing records a request for NYSE suspension; it does not state the exact final trading session. [Issuer completion filing, Items 2.01/3.01](https://www.sec.gov/Archives/edgar/data/1283630/000119312522216177/d390151d8k.htm), [acquirer completion](https://www.blackstone.com/news/press/blackstone-funds-complete-13-billion-acquisition-of-american-campus-communities/). | 3,172 rows through 2022-08-09. Exact last trading remains unverified; the merger date alone cannot classify that final row as valid or invalid. |

Result: **expected CLI exit 1, `valid: false` for the fixed sample**, with four rows across three symbols after independently stated last trading dates. Repeated output is byte-identical. These rows need vendor explanation; they are not automatically deleted or treated as executable quotes, returns or payouts. Two requests do not reach the reviewed later event; ACC lacks an independently established exact final session; two consideration structures are unsupported. These categories overlap and are not six independent failures of one kind.

The earlier listing audit's five-candidate description and endpoint-alignment wording were too broad. Its adjustment consistency totals remain reproducible, but ABST's 2020 window cannot support a 2023 terminal-history claim. Producer-derived ABMD identity dates in earlier artifacts are likewise coverage-derived, not independently verified exchange dates. Those immutable artifacts are retained as historical evidence, with this correction linked from their review notes. No existing dataset is silently rewritten.

## Validation and remaining gate

Authored unit cases cover clean final-session comparison without using the close as payout, post-trading rows on completion/suspension dates, missing sessions versus short request windows, unreviewed dates, continued listing, mixed stock/CVR consideration, empty/out-of-window/future histories, source corruption, missing captures, same-instant URL/content conflicts, retransmissions, capture metadata corruption and invalid reference semantics. Genuine acceptance is limited to the six exact captured payloads and the declared source assertions; no accelerator, strategy trial, genuine final holdout or broker request was used.

Local validation: 550 .NET tests and 37 Python tests passed; Ruff, the research-boundary check, Svelte check/build and the full-history/files secret scan with its synthetic leak control passed. The new .NET tests use authored financial inputs; the separate offline audit uses genuine captured histories. No accelerator changes or device benchmarks were made.

Next evidence work is to resolve the post-trading row semantics with the data provider, obtain independently verified last-session facts for ACC and deliberately budget complete terminal-period history requests where needed. Usable terminal action observations also need source-bound first availability, actual consideration/settlement semantics, revisions, currency and a supported accounting path. These findings block treating the sample as accepted terminal-return coverage.

A [separately declared ABST follow-up](terminal-outcome-follow-up.md) corrected that one original request window and found another post-last-trading row. It preserves all 22 earlier rows unchanged and does not replace this fixed six-capture record.

## SEC corroboration and explicit dispositions (21 October 2026)

V1-02 accepts declared terminal coverage that is either independently accepted or explicitly refused, provided failed names are not dropped. This checkpoint adds both halves without changing the earlier reviewed dates or captured histories.

**SEC terminal filings.** `sec-collect` now takes `--forms terminal` (8-K/8-K/A, Form 25/25-NSE and amendments, DEFM14A/DEFM14C and 15-12B/15-12G/15-15D). These carry no report period, so they are review sources and never become periodic `Filing` records. With the owner-designated contact set privately in `TWENTY_SEC_CONTACT`, five bounded runs (AAIC, AAWW, ABMD, ABST, ACC; ACB is not terminal) made 29 collector requests and archived 24 original filings, with no history or document limit reached. Ten manual read-only requests looked up CIKs and sized the EDGAR full index. Originals stay private; their URLs, receipt times and hashes are in the [new reference bundle](terminal-outcome-references-2026-10-21.json). The 20 October bundle is unchanged and still reproduces its report byte-for-byte (`1d696373…`).

What the filings establish:

| Symbol | SEC evidence | Effect |
| --- | --- | --- |
| AAIC | Completion 8-K: merger consummated 2023-12-14; 0.3619 EFC shares plus USD 0.09 cash per common share. NYSE 25-NSE filed 2023-12-14. Neither states the 2023-12-13 last trading date | Consideration now from an original filing, still mixed. The last trading date has only the Ellington web extraction as a direct statement |
| AAWW | Completion 8-K: merger consummated 2023-03-17 for USD 102.50 cash; Nasdaq notified 2023-03-14 and filed Form 25 on 2023-03-17 | Shares ceased to exist on 2023-03-17, independently of Nasdaq's notice. The 2023-04-03 vendor row cannot be a session |
| ABMD | Completion 8-K: Nasdaq asked to suspend trading "effective as of the close of business on December 21, 2022"; USD 380 cash plus one non-tradeable CVR | Independently states the 2022-12-21 final session. The CVR remains unsupported |
| ABST | Nasdaq 25-NSE accepted 2023-07-27 12:54 UTC, before the US open | With the issuer's completion release and Nasdaq ECA 2023-425, three original sources from three hosts bound trading to 2023-07-26 |
| ACC | Original completion 8-K (replacing the earlier web extraction as evidence): suspension requested on 2022-08-09. The 2022-08-04 8-K expected closing on 2022-08-09. NYSE 25-NSE filed 2022-08-10 | None states whether 2022-08-09 traded. The final session stays unverified |

**Dispositions.** `terminal-history-audit … --dispositions <file>` (`terminal-disposition-v1`) assigns every audited symbol exactly one outcome. The [declared disposition file](terminal-dispositions-2026-10-21.json) must list exactly the audited symbols and cite only each subject's own sources. A subject is **accepted** only if it is a completed cash-only merger, the request covers the event and final session, the final-session bar is present, and the last trading date is cited from at least two original responses on different hosts. Vendor rows after that date are then listed as **refused rows**: not executable sessions, returns or proceeds, and not deleted from captures. Everything else is **refused** with the audit's reason codes. The raw audit result (`auditValid: false`) is reported unchanged alongside.

| Symbol | Outcome | Final session | Reasons | Refused rows |
| --- | --- | --- | --- | --- |
| AAIC | refused | — | consideration-not-supported, last-trading-corroboration-insufficient | 2023-12-14 |
| AAWW | **accepted** | 2023-03-17 | — | 2023-04-03 |
| ABMD | refused | — | consideration-not-supported | 2022-12-22, 2023-01-03 |
| ABST | **accepted** | 2023-07-26 | — | 2023-07-27 |
| ACB | refused | — | not-a-terminal-event, request-does-not-reach-event | — |
| ACC | refused | — | last-trading-date-unverified | — |

Result: 2 accepted, 4 refused, none dropped. Exit 0, and repeated output is byte-identical. [Compact evidence](terminal-disposition-audit.json) binds the reference, dispositions, audit report, output and every collected SEC accession/hash.

Limitations: this is a six-symbol declared sample, not market coverage. AAWW's acceptance uses Nasdaq's stated final session plus the 8-K's same-day consummation. It establishes when trading ended, not cash payment timing, taxes or FX. The changing-universe backtest still does not consume these outcomes (V1-05). Price conversion does (V1-03): `listing-prices` v5 takes this report and refuses to convert the four listed rows, or any row after an accepted subject's final session, counting them as `TerminalRowSkipped` — see [price conversion](listing-prices.md#terminal-disposition-bounding-21-october-2026). The report's own "does not yet filter dataset conversion" clause described the 21 October state and is superseded by that step; the report bytes are unchanged so `dispositionsSha256` still verifies, and the clause should be dropped whenever the disposition schema next advances. The per-year Form 25 coverage denominator is still unmeasured: quarterly `form.idx` files are 39–50 MB uncompressed (about 4–5 MB as `form.gz`), so it needs a separate bounded index collector. The vendor's meaning for the five later rows remains unexplained; refusing them does not explain them.

Validation: 8 new authored tests (one terminal SEC selection case and seven disposition cases, including corroboration theories and exact-coverage refusals). Inputs are authored except this offline genuine audit; no accelerator, model trial, final holdout or broker request was used.
