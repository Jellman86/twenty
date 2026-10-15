# Bounded SEC filing collector

Implemented in `Twenty.Ingestion.Sec`, invoked explicitly through `Twenty.Data`. Reviewed against SEC documentation on 15 October 2026. This collector archives submission metadata, linked older history and selected primary reports. It does not collect Company Facts, extract report sections, calculate signals, schedule work, or call a broker. The deployed web image does not contain this CLI or start collection.

## Current verification

Automated HTTP tests use in-memory handlers and authored synthetic fixtures under `fixtures/sec`. They exercise the collector, archive and chronological selection without making SEC requests. No live collection has been validated yet. Before a live smoke test, supply a genuine monitored contact email in private local configuration. Syntax validation cannot establish ownership or that a mailbox is monitored.

The SEC permits scripted access, currently limits access to 10 requests/second and asks clients to identify their User-Agent with a contact. Twenty chooses a more conservative shared two requests/second and one in-flight request. [SEC access guidance](https://www.sec.gov/search-filings/edgar-search-assistance/accessing-edgar-data), [developer FAQ](https://www.sec.gov/about/webmaster-frequently-asked-questions).

## Commands

Offline inspection (no contact or network required):

```sh
dotnet run --project src/Twenty.Data -- sec-inspect 1 fixtures/sec/submissions.synthetic.json
```

For live collection, set `TWENTY_SEC_CONTACT` privately to the monitored address; do not commit the address, a populated environment file, downloaded payloads or collector output. Then run, replacing the CIK and date window as appropriate:

```sh
dotnet run --project src/Twenty.Data -- sec-collect 320193 --from 2024-01-01 --to 2024-12-31 --archive data/sec
```

CI never sets a real contact or runs that live command. The CLI refuses a missing/invalid/obvious placeholder contact before opening the archive or creating an HTTP client. It prints counts, coverage-limit flags and safe error codes, not contact details, headers, response bodies or framework exception text. Ctrl+C cancels collection. A nonzero exit means collection failed; already validated originals remain available for a later retry, but there is no successful catalogue for the failed run.

## Boundaries and budgets

| Control | Default | Hard configurable ceiling |
|---|---:|---:|
| Request starts, both SEC hosts and retries | 2/second | Fixed |
| Concurrent HTTP requests | 1 | Fixed shared process gate |
| HTTP attempts per resource | 3 | Fixed |
| Network attempts per client/run | 25 | 100 |
| Linked history files per run | 3 | 10 |
| Primary reports per run | 5 | 20 |
| Decompressed bytes per response | 8 MiB | 8 MiB |
| Archive disk usage | 256 MiB | 1 GiB |
| Quarantined entries | 20 | 100 |
| Quarantined bytes | 16 MiB | 64 MiB |
| Fresh-cache lifetime | 6 hours | 7 days |
| Request timeout | 30 seconds | 60 seconds |
| Run timeout | 5 minutes | 10 minutes |

The CLI currently uses defaults; library limits are configurable for tests and later job orchestration. Quarantine sizes include JSON/base64 overhead. An oversized response is rejected without claiming to archive a complete original. Rejected complete responses are quarantined when they fit the quarantine budget; an over-budget rejection fails closed. Archive exhaustion stops collection rather than evicting accepted evidence. Keep bounded private backups of irreplaceable provenance when real research starts to depend on it.

Use one archive root and one collector process for the outbound IP. An exclusive archive lease prevents concurrent writers, and the in-process gate covers all clients/SEC origins/retries. Separate roots on separate processes or unrelated tools do not share that gate; no estate-wide rate-limit coordinator is implemented. Rate limits apply across the SEC traffic originating from an IP, so account for other workloads.

Only generated HTTPS URLs under `data.sec.gov/submissions/` and `www.sec.gov/Archives/edgar/data/` are used. CIKs, accession formats, history filenames and relative document paths are validated. Queries, credentials, alternate ports and off-origin requests are rejected; automatic redirects and cookies are disabled by the production HTTP client. The injectable test client must preserve those transport restrictions if reused outside tests.

429 and 500/502/503/504 responses have bounded retries. Retry-After delta/date is honoured up to 60 seconds; a longer request aborts rather than retrying early. Otherwise 429 waits 15 seconds and server/transport errors use increasing delays. Access denial and redirects stop immediately. Connection/read timeouts and cancellation are bounded. This is one bounded historical collector, not a real-time filing feed.

## Parsing and historical scope

The current submissions URL uses a ten-digit zero-padded issuer CIK. Its `filings.recent` parallel arrays and `filings.files` older-history links are preserved in raw bytes. Matching history ranges are visited newest first, then reports are selected by filing date and acceptance time. History-count/date mismatches and conflicting duplicate accessions stop collection. Exact duplicate rows are deduplicated. Unknown upstream fields remain in originals; they do not become domain defaults. [SEC API specification](https://www.sec.gov/search-filings/edgar-application-programming-interfaces).

V1 selects 10-K, 10-Q, 20-F and 40-F, including `/A` amendments. It archives each primary document, not every exhibit or complete dissemination package. All required arrays and row identities must be valid, including non-selected rows; missing primary-document paths or unzoned acceptance timestamps cause quarantine rather than guessed URLs/times. Broader historical variants need captured, reviewed fixtures and a parser-version change. An accession prefix may identify a filing agent, so it is not required to equal the issuer CIK.

`historyLimitReached` and `documentLimitReached` disclose truncation. A false flag only means the configured cap did not exclude a matched item; it does not certify complete SEC coverage, correct upstream range metadata, or full issuer history. Current submissions are not a historical security/ISA eligibility directory. CIKs identify filing entities, not individual listings. This US reporting source does not cover every UK/global ISA security.

Amendments retain a separate accession and form flag. V1 does not infer which report an amendment changes, or replace the original report. Company Facts/XBRL fact contexts, custom taxonomy/segment handling, comparable report sections and verified amendment links remain later ingestion/model work.

## Storage, caching and revisions

Under `<archive>/sec-v1/`:

- `objects/<sha256>.bin`: exact decompressed response payload bytes, written atomically without replacement; repeated identical originals share storage.
- `receipts/<url-hash>-<content-hash>.json`: immutable URL, original hash, first observation, content type, selected cache validators and parser version. Contact headers are absent.
- `cache/<url-hash>.json`: replaceable pointer and last successful check time, verified against its immutable receipt and original hash before use.
- `catalogues/<sha256>.json`: immutable successful run results binding metadata and document receipts; each run can create a new audit record even when originals are reused.
- `quarantine/`: bounded files with response bytes, safe failure code, URL, observation time and hash; oldest entries are pruned before accepted evidence.

The root and files use owner-only permissions on Unix. Successful payloads are validated before archive/cache publication. Old versions remain after upstream changes. Fresh cache avoids HTTP; stale cache uses ETag/Last-Modified and accepts 304 only with a verified cached original. Identical content retains its first observation, while a changed payload gets a new receipt. Corrupt cache/original evidence stops the run, without an automatic network overwrite. Metadata is private local evidence, not a cryptographically signed or adversary-proof audit log. Durable searchable registry/jobs remain #7.

## Timing and domain import

Acceptance is retained separately from observed public availability. SEC documents a typical website lag but explicitly gives no guarantee or exact first-public timestamp. Twenty never adds a fixed lag to acceptance and labels it an observed historical release. [SEC timestamp/latency guidance](https://www.sec.gov/about/webmaster-frequently-asked-questions).

Each collected filing becomes usable no earlier than the later first observation of its exact metadata payload and document. `KnownAt` excludes future observations and refuses decisions after the catalogue cutoff. Downloading a report from 2020 today does not make it available to a 2020 feature pipeline under this observed-evidence policy. A historical backfill policy needs independent publication evidence or a separately documented conservative timing policy and sensitivity tests.

`ToDomain` emits a v1 filing observation only with a report period and when its metadata was already known by the document receipt. Its publication/availability value is the observed bound, not a claim about the original dissemination instant. If updated metadata was first seen after a cached document, the catalogue retains both clocks but direct conversion refuses: a reviewed composite-source/revision import is required. Verified company/listing identity, calendar/universe metadata and correction ancestry must be supplied before building a full dataset; the collector does not invent them. No collected filings are automatically imported into a model or portfolio.
