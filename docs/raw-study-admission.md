# Atomic raw-study admission and shared reservations

This records the admission checkpoint. The subsequent [explicit development worker](raw-study-development.md) adds start/report endpoints and changes execution availability to true; final evaluation remains unavailable.

The [frozen raw-study declaration](raw-universe-study-declaration.md) now has a separate durable admission path. One SQLite transaction retains its canonical original plan, complete prior model/universe/raw trial disclosure, all eight unstarted development entries, their request bindings and the registration event. No raw calculation or final evaluation starts. The raw runner, selection/report workflow and final spending endpoint remain unavailable.

## Original-input admission

`RegisterRawStudy` takes a UUID, bounded name and a canonical plan, supplied directly to the domain or referenced by SHA-256 in the private inbox. HTTP admission uses the small hash reference; it does not raise the application's 16 KiB request-body limit. Put encoded plan bytes in `imports/<sha256>.raw-study.json`. It verifies the declaration under the current Core/runtime and scientific identity, the imported dataset's exact bytes and validation contract, actual synthetic provenance, declared universe and creation cutoff. An otherwise internally consistent client assertion cannot turn synthetic inputs into research provenance.

Original session tables belong in the private `imports/<table-pin>.calendar.json` inbox. The pin is the existing semantic `ExecutionCalendar.Sha256`, not the wire-byte digest. Strict parsing reconstructs and verifies that table, its venue/reference and full declared date coverage. Every listing in the input file must have a matching pin; this conservative rule does not silently prune unpinned listings. Admission separately records and retains the exact original calendar wire bytes and their SHA-256. The existing calendar engine accepts at most 10,000 consecutive days per table; this independent bound can refuse a metadata-valid 600-date declaration. Admission does not shorten its horizon.

The original action catalog belongs in `imports/<sha256>.actions.json` as an array of `RawActionSchedule` records using the raw replay JSON format. Its pin hashes exact bytes. Admission validates bounded framing, unique subjects/bookings and matching observation/listing/action IDs and kinds in the original dataset. Booking points must name that same dataset. It does not certify source-wide completeness, legal entitlements or successful eventual scheduling/accounting. Those checks remain separate; malformed or unsupported economic assumptions cannot become certified outcomes merely through registration.

Inputs are bounded at 8 MiB each and 32 MiB in aggregate, including the dataset. Calendar/action originals are retained under content hashes outside the inbox; the dataset remains in its existing immutable import archive. An admitted retry does not reread a mutable inbox or replace its original evidence. The retained input manifest distinguishes semantic pins from original wire hashes, and reads verify byte length and hash. No dataset, account data or generated registration belongs in Git.

## Atomicity, retries and recovery

Admission holds the shared store gate and uses the existing single-writer lease and checks global study/trial capacity, prior exposure and active/permanent holdout controls before committing work. All eight rows and their exact `RawStudyTrialRequest` artifacts bind the parent plan digest, trial role and development stage. SQL failure rolls back the complete family. File failure before commit also leaves no admitted family; bounded orphan files may remain and are not automatically promoted or deleted. File flush/rename and SQLite transactions do not constitute full power-loss, backup or filesystem recovery acceptance.

The same request UUID with the same name and plan digest returns the original registration, including after cancellation or inbox removal. Changing its meaning conflicts. Concurrent retries converge on one family. No interrupted request is silently replaced by new research results, and no unstarted raw row is automatically executed after restart.

Historical registration reads preserve exact plan bytes without requiring their old build to become today's implementation. New admission still requires the current scientific/Core/runtime identities. An authored older-build snapshot regression checks this inspection/retry distinction; it is not a claim that an actual older raw-study binary has passed full migration acceptance.

## Shared search disclosure and holdouts

Raw studies share the existing sixteen-study lifetime ceiling and thousand-trial budget. Eight development entries count immediately, and two final slots are reserved while the registration is active. Ordinary model/universe admission and both older study protocols include these counts. Missing or inconsistent admitted raw rows fail closed rather than silently freeing capacity.

Genuine active raw final windows block overlapping model/universe/raw work across dataset aliases. Raw admission likewise respects older active reservations and permanent spent claims. Prior raw development requests remain known exposure, including after cancellation. Date windows use exact UTC instants and the existing half-open overlap rule; the development footprint includes its historical price-age lookback.

Cancellation atomically marks the family cancelled, releases its active reservation/two reserved slots and retains every trial and original request. It never creates a replacement final evaluation. Existing study declarations include prior raw trials; old records with no raw snapshot retain their original JSON representation. New diagnostics disclose raw entries explicitly as a different research engine, without treating uncomputed raw trials as comparable returns or silently dropping them. The workbench's universe-study disclosure includes raw counts when present.

Once raw registrations exist, older writers that ignore these tables are not compatible with the workspace's shared reservation/budget contract. Recovery must retain the new tables and use a writer that understands all registers. No backward-writer safety or initial-v1 operational completion is claimed.

## API

Writes retain the existing same-origin/header admission policy.

- `POST /api/research/raw-studies`: `{ requestId, name, planSha256 }`; imports the hash-bound inbox plan and registers only.
- `GET /api/research/raw-studies`: retained declarations, with `executionAvailable: false` and `finalEvaluationAvailable: false`.
- `GET /api/research/raw-studies/{id}` and `/plan`: inspect the record or exact original plan bytes.
- `GET /api/research/raw-studies/{id}/events`: bounded lifecycle journal.
- `POST /api/research/raw-studies/{id}/cancel`: retain and cancel the family.
- `GET /api/research/raw-trials` and `/{id}/request`: inspect all retained raw entries and exact requests.
- `GET /api/research/trials`: total, model, universe and raw counts.

Plan authoring must use the server-matching build/scientific identity. This is an API admission checkpoint, not a new graphical raw-study editor or executable strategy workflow. There is no raw execute/evaluate endpoint, broker connection, automatic purchase, training or genuine final evaluation in this change.

## Authored validation

Regressions cover complete admission, SQL rollback, file failure, retry conflicts and concurrency, inbox removal, retained-input tampering, cancellation, restart, actual provenance, calendar/action bindings, older permanent claims, cross-engine reservations, both capacity directions, complete prior/diagnostic disclosure, missing admitted rows and the HTTP write guard. A temporary bypass of the raw reservation check must be caught before the restored full suite is accepted. Financial fixtures and false synthetic flags are authored guard tests, not genuine market evidence.

Local acceptance passes 1,229 .NET tests, 45 Python tests, Ruff, zero-error/warning Svelte checks, the web build, scientific-boundary and sensitive-path/secret guards. The cross-engine reservation bypass was caught before restoration. Full-host rehearsal caught the original oversized HTTP plan carrier; the endpoint now resolves a bounded inbox hash reference under the unchanged 16 KiB host limit. A fresh private host recovered eight registrations from an actual earlier local Research assembly, preserved exact plan/request identities, refused a new UUID under the old scientific plan, and retained cancellation/retry identity after restart and inbox removal. Original/current assembly and scientific hashes are retained. No raw execution or final claim occurred. A final older completed-study clone preserved its plan and ten request/report pairs and kept its final lock. Private proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/raw-study-admission-20261025` and `universe-study-cross-version-raw-admission-http-final-20261025`. This is a bounded private rehearsal, not full-volume/authenticated production migration acceptance.

## Publication and deployment acceptance

Source `19f71d7174ab6f4c9cd2736295ab0a9a26d328d9` passed [CI 40205628753](https://github.com/Jellman86/twenty/actions/runs/40205628753), including all tests, security gates and image smoke/publication. The rolling digest matched the commit image before Git-backed Dockhand deployment. Fresh checks found no active research work. Live metadata reports that exact source and one healthy container. Eight existing collections, 58 byte-identical reports, study-plan bytes, four diagnostic responses/register and seven paper accounts were preserved. Counts remain 61 model, zero universe and zero raw trials. The new raw registers are empty; execution/final evaluation flags are false. Broker connection and automatic purchases remain disabled. No production registration, financial calculation or final evaluation was performed as deployment validation. Private proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/deploy-raw-admission-20261025`.

This accepts bounded admission, source binding, shared controls and preservation. The registered raw execution worker, candidate scoring/diagnostics, permanent raw final spending, reports and paper-account integration remain open, as do genuine-input and final operational acceptance.
