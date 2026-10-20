# Research checkpoint recovery rehearsal

20 October 2026, Quark, source checkpoint `ef9dee848453f89b09ea16cc1275e75357bd7dce`. This is bounded evidence for [V1-07/V1-08](initial-v1-acceptance.md), not completion of those gates or certification of an independent backup.

## Private clone and refusal controls

The deployed research store was quiescent, with no queued/running model work or active study. `research.db` had no WAL before or after its read-only copy, and its source SHA-256 stayed unchanged through capture. The database, datasets and run artifacts were copied through read-only Docker diagnostics into private SSD-backed developer storage. SQLite `quick_check` returned `ok`. This method is specific to that verified quiescent/no-WAL state; it is not a safe hot-copy procedure for a writing SQLite database.

The current source build opened that copy on `127.0.0.1` with collection off, no broker credentials and no execution configuration. Six state collections matched their HTTP baselines: datasets, model trials, paper accounts, universe trials, signals and studies. All 58 successful report responses, four diagnostic responses and exact study-plan bytes were preserved. The Core source addition changed scientific assembly identity as expected without rewriting completed evidence. No trial, final request, financial request or new capture was created by reading the clone.

Three write requests without the required header, with a foreign Origin or with cross-site fetch metadata were rejected with `same-origin-required`; the trial registry remained unchanged. Security headers included `nosniff` and the frame-ancestor restriction. A report was deliberately altered only in the private copy: its read failed with `report-integrity` before and after restarting that local process. The application left the corrupt file unchanged. Manually restoring the clone's original bytes restored its original response; the production report was independently checked unchanged. The local process was stopped after each rehearsal.

Full private records are in `artifacts/twenty/recovery-20261020`, outside Git. They include the quiescent-copy verification, response checks and integrity/security negative-control results. Authentication, broker evidence, original captures, active-work recovery and a mounted-volume restore were not rehearsed here. This developer-state copy is excluded from the ordinary app backup and does not establish another durable backup location.

## Published image and deployment

[CI 39507796734](https://github.com/Jellman86/twenty/actions/runs/39507796734) passed full-history/files secret checks, .NET/Svelte/Python validation, the scientific-boundary check, built-image smoke and publication for `ef9dee8`. Local validation passed 588 .NET tests, 39 Python tests, Ruff and Svelte check/build with zero warnings/errors. The new financial inputs were authored fixtures; no accelerator changes or genuine final evaluations were used.

The rolling image manifest matched the immutable SHA tag before deployment. Dockhand resolved the Git-backed Twenty stack, synced and hash-verified its Compose source, and completed one successful API-owned deployment. A single Twenty container is running healthy. Readiness and metadata identify the expected checkpoint. All six collections, 58 report responses, study plans and four diagnostics remained unchanged across deployment; the 61 original model trials remain disclosed, with no universe trials and automatic purchases disabled. The new scientific identity is recorded and registered universe selection remains false.

A live read-only browser smoke opened the research view, confirmed the empty universe history and checked the deployed metadata/capability at desktop and 390-pixel widths. It found no page errors, horizontal overflow or research/execution writes. T3 preview opening explicitly failed because host browser libraries were missing; the existing authenticated shared browser was used without administrative changes. Private deployment, API and browser receipts are in `artifacts/twenty/deploy-universe-plan-20261020`.

Persistent universe study admission, complete selection/final-spend controls, raw-price/GBP-FX accounting, terminal-data acceptance and the final combined workflow remain open. This checkpoint creates no release and enables no live execution.
