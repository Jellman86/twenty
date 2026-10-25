# Registered raw development worker

The raw-study API now supports an explicit development start after [atomic admission](raw-study-admission.md). Admission alone does not execute anything. This is a bounded research calculation, not final evaluation, a selected investment strategy or broker execution. The initial-v1 accounting, combined acceptance and genuine-data gates remain partial.

## Execution contract

`POST /api/research/raw-studies/{id}/start` accepts `{ requestId, planSha256 }`. The UUID identifies one irreversible start request for the exact admitted plan. An identical retry returns the retained family, including after completion, cancellation, restart or removal of the import inbox. Another start UUID cannot replace an attempted family. New starts and queued execution require the original scientific, Core and runtime identities to match the running build; historical inspection does not reinterpret old results under new code.

The worker records `started` before loading original inputs or calculating results. It reads only the retained, hash-checked dataset, calendars, action catalog and trial request. All eight candidate/cost entries remain registered. Each trial calculates independent empty momentum, equal-weight and cash accounts with the frozen development deposits, reserve, freshness limits and fill policies. Decision-time slices determine signals, weights and share intentions; subsequent slices provide reviewed-session fills and marks. The final window receives no development funding or evaluation.

The first supported scope explicitly refuses economic corporate actions known within the evaluated development window and order batches spanning multiple calendars. It does not selectively remove affected securities or candidates. Missing raw bars, executable-purpose FX, rejected orders, insufficient cash or any incomplete bound fill batch fail the attempted trial and stop its remaining family. Later final-window action rows do not gate development. Source-wide action discovery and action-bearing study scheduling remain required work.

Orders use the first supplied reviewed open after the decision, bounded by the next decision. Each batch is booked once the session closes and all selected raw bars are available; replay requires this same booking time. Sell-first funding, fractional quantities, historical FX labels and volume screens remain model assumptions. Calendar tables are stored once per replay request and referenced by their semantic hash; existing inline-calendar replay requests remain supported. Existing archive size and work limits are unchanged.

## Lifecycle and integrity

Parent states are `registered`, `development`, `development-complete`, `failed` and `cancelled`. Child states retain planned, started, succeeded, failed, interrupted and cancelled attempts. A failed attempt is never replaced. A process restart interrupts a started attempt and stops its unstarted family; explicitly queued, unstarted work may resume. Cancellation preserves completed reports, requests and attempted rows, cooperatively stops a running calculation, and rechecks cancellation before publishing any report. An unresolved started row cannot be bypassed by starting another raw attempt.

The worker shares the existing computation lease and two-minute deadline. Queue order uses the explicit raw start time, rather than the earlier admission time, against model/universe admission timestamps. Active registered, developing and completed-but-unselected raw studies retain their shared reservation and two final slots. Cancellation/failure release unspent reservations but retain the original trial exposure and lifetime counts. No raw final claim is created.

`GET /api/research/raw-trials/{id}/report` returns an immutable SHA-bound report with three account summaries and original replay receipts. Before publication a fresh replay independently verifies the exact plan bytes, role, deposits, complete window, strategy decisions, policies, ordered fills and marks, then recomputes balances and summaries. Recovery also binds the report limitations. Historical report inspection checks retained hashes and metadata without reexecuting an old scientific build.

`GET /api/research/raw-studies` advertises `executionAvailable: true`, `finalEvaluationAvailable: false`. Writes retain the existing same-origin/header guard and 16 KiB host body limit. There is no raw final-evaluation endpoint, raw graphical authoring workflow, selection, paper-account integration, automatic purchase or live trading in this checkpoint.

## Verification

Authored fixtures exercise independent hand arithmetic: 35 deposits of £20 produce £700 with flat raw prices and zero costs. With a £0.25 fee, the single-asset momentum account pays £8.75 and ends at £691.25; two-asset equal weight pays £17.50 and ends at £682.50; cash remains £700. These are engineering examples, not historical performance.

Regressions cover full-window replay, changed deposits, altered limitations, missing/duplicate calendar tables, cancellation, preserved successful reports, missing retained inputs, interrupted-attempt restart, queue timing, explicit action refusal and byte-identical report recovery. Multiplying prices strictly after the development end by 100 leaves all development months unchanged. The full .NET suite passes 1,241 tests; 45 Python tests, Ruff, Svelte checks/build, secret/path scans and the isolated scientific-boundary probe pass. No optional neural or real-device accelerator path changed.

Private cold-host HTTP acceptance is retained under `/mnt/apps/developer/jellman86/artifacts/twenty/raw-study-worker-20261025`: authored input preparation, explicit start after inbox removal, eight original trials, report hashes, UUID replay and report identity after restart. No genuine holdout or production experiment is used for this rehearsal. Deployment evidence is recorded separately once available.

## Publication and deployment acceptance

Source `37064893ac278935c7013af935737e90e33e55a9` passed [CI 40233900402](https://github.com/Jellman86/twenty/actions/runs/40233900402), including image smoke tests and registry publication. The rolling `dev` manifest matched its source-specific image tag before one Git-backed Dockhand sync/deploy. Public readiness returned HTTP 200, the expected source commit was served, exactly one Twenty container was running healthy, and no startup failure markers were found. No release merge or tag was created.

The before/after receipt at `/mnt/apps/developer/jellman86/artifacts/twenty/deploy-raw-worker-20261025` preserves all eight application collections, 58 byte-identical report responses, stored report hashes, original study plans, four byte-identical diagnostics and seven paper accounts. Trial counts remained 61 model, zero universe and zero raw. Production had no active studies before deployment; no production experiment was registered to demonstrate the worker. Raw execution availability changed to true, raw final evaluation remains false, and automatic purchases remain disabled.

Separate private compatibility rehearsals preserve the actual older raw-admission workspace's eight cancelled entries, original requests/plan and same-UUID admission replay without its inbox. The older completed universe-study rehearsal preserves ten original request/report pairs, allows its already-spent final UUID to replay and rejects a replacement final UUID. These bounded checks do not establish backward-writer compatibility, full disaster recovery or initial-v1 completion.
