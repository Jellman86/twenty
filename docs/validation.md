# Validation record

## Boosted-tree challenger (18 October 2026)

Hypothesis `tree-relative-return` adds a deterministic gradient-boosted-tree challenger to the ridge model (fixed 60-tree/depth-3/min-leaf-20/learning-rate-0.1 constants, no tuning fields), with per-fold tree snapshots, frozen forward-signal scoring and a sleeve form; it stays outside the frozen `contribution-study-v1` family, so no live-execution path can use it. Twelve new tests cover determinism, a pure-interaction target, constant inputs, snapshot corruption, chronology/future-perturbation, report round-trip plus legacy fold JSON, the tree sleeve and tree forward signals. A declared 12-trial ridge-vs-tree comparison (base/stress costs, development windows) is recorded in the [tree challenger record](tree-challenger-2026-10-18.md): on the real French industry panel the tree (10.75%) trailed the ridge (11.03%), and both trailed the benchmark (12.63%) and equal weight (12.60%); on authored synthetic panels the tree recovered nonlinear structure the ridge cannot represent, which is software evidence only. Local validation passes 335 .NET tests, 22 Python tests (2 optional neural skipped), Ruff, Svelte check/build, the research-boundary probe, the dataset validator, the SEC/ECB inspectors and the sensitive-path guard. Financial inputs were synthetic fixtures plus the previously captured French research panel; no broker order, holdout evaluation or Quark deployment was part of this work.

## Listing intervals from daily directory captures (18 October 2026)

Daily `nasdaq-listed`, `nasdaq-other` and `tiingo-tickers` captures now convert offline into [observed listing intervals](listing-intervals.md): per-source first/last-seen segments keyed by producer start date, asset type and currency, with absence and >7-day continuity gaps closed explicitly, reappearances never merged, and no delisting dates or cross-source identity inferred. Fourteen new tests cover both Nasdaq formats, the Tiingo segment file, interval/gap/revision semantics, same-instant conflicts and deterministic rebuilds. The parsers were also run against one live capture of each genuine directory (private scratch archive, not committed): 122,262 intervals, with real-file behaviours — padded Nasdaq names, 9 byte-identical Tiingo rows deduped, 69 same-segment end-only repeats collapsed, reused-ticker segments kept apart — recorded in the audit and the interval document. Local validation passes 323 .NET tests, 22 Python tests (2 optional neural skipped), Ruff, Svelte check/build, the research-boundary probe, the dataset validator and the sensitive-path guard; the Gitleaks script itself is Linux-only and remains a CI gate. No broker order, holdout evaluation or Quark deployment was part of this work; interval coverage on Quark compounds from its first listing capture.

## Execution gates, purchase-cap semantics and assumption tests (18 October 2026)

Live broker execution now requires a registered-study final run (`execution-study-required` for non-study live policies; demo rehearsal unchanged), reserved-holdout reports cannot seed forward signals without verified study approval (`signal-report-scope`), the contribution weight cap is a purchase cap in both `NotionalPortfolio` and `LimitPlanner` (drifted holdings are never added to; contributions buy underweights; sub-minimum scaled-buy cash is reallocated), and the web UI reports an unknown execution mode instead of asserting the safe mode when `/api/meta` or Settings reads fail. Local validation passes 309 .NET tests, 22 Python tests (2 optional neural skipped), Ruff, Svelte check/build, the research-boundary probe and the dataset validator. Declared assumption tests on the real French industry panel are recorded in [assumption tests](assumption-tests-2026-10-18.md): no candidate beat the market benchmark in either engine, and the reserved holdout remains unspent. A second research panel (French 25 size/value portfolios, `french-size-value25-gbp-v1`) and a `core-satellite` candidate followed the same day; [novel-approach tests](novel-approaches-2026-10-18.md) record small growth last of 25 corners and no candidate beating the market. Multi-strategy sleeve allocation (`sleeve-blend`) and [declared blend tests](sleeve-blends-2026-10-18.md) followed: blends interpolate their sleeves and none beat the market on both panels. No broker order, live or demo, was placed by this work.

## Astra review fixes and ECB FX audit

An independent [Astra review](reviews/2026-10-15-astra.md) reproduced two P1 defects in the otherwise passing foundation: backdated observed availability and a mixed SEC page rejected by a valid relative Form 4 path. Both are fixed with regression cases. The [ECB parser/audit](ecb-fx.md) adds synthetic tests plus a separate real-source sample check across two ECB delivery formats. Local .NET validation now passes 128 tests; Astra independently reran those tests and the stored ECB samples in a follow-up review, finding no new actionable defects. CI run [38772982226](https://github.com/Jellman86/twenty/actions/runs/38772982226) passed tests, secret scanning, container smoke and publication. No live SEC collection or historical-vintage audit is claimed.

## SEC collector implementation

The bounded SEC collector adds offline HTTP/archive tests for linked history, amendments, first-seen timing, caching and restart, retries/rate limits, cancellation, storage bounds, quarantine, unsafe URLs and integrity failures. Local validation on 15 October 2026 passed 89 .NET tests (43 added for this collector), 22 Python tests with optional neural dependencies, Svelte checking/build and Ruff. These tests use authored synthetic responses; no live SEC request or real-data coverage audit has been completed. See [collector scope and operating limits](sec-collector.md).

## Dataset foundation validation

The C#/.NET and Svelte workbench is implemented. Dataset foundation commit [`65ac919`](https://github.com/Jellman86/twenty/commit/65ac91986892ff9f7b5119e26e79062fc485141b) adds the [versioned data contract](data-contract-v1.md), point-in-time selectors, immutable archive and offline validation CLI.

- 46 .NET tests pass, covering the existing contribution calculations and the new contracts, availability/revision selection, currency units, provider shapes and archive integrity.
- 22 local Python tests pass with optional PyTorch installed. Core CI runs the same suite with two optional neural tests skipped. Ruff checks pass.
- The CLI accepts and archives the synthetic fixture, rejects invalid inputs and avoids echoing untrusted JSON property names in parse errors.
- [CI run 38764570834](https://github.com/Jellman86/twenty/actions/runs/38764570834) passed .NET, Svelte and Python checks, full-history/file secret scanning with a deliberate fake-secret rejection control, and the container smoke test before image publication.
- The research web container was deployed through Dockhand on Quark and passed the HTTP smoke checks; its metadata reported commit `65ac919` and `brokerConnected: false`.

These checks use synthetic data and an illustrative calendar fixture. They do not establish historical coverage, source availability accuracy, licensing rights, trading profitability or accelerator support. At this milestone, provider collection, persistent quarantine and complete exchange calendars were future work; the subsequent SEC collector is described above. No broker integration or GPU training job was deployed.

## Initial Python validation (historical)

15 October 2026, local Apple M5 / 16 GB machine, Python 3.12.13, NumPy 2.5.4 and optional PyTorch 2.14.1.

- At this stage, 20 unit/integration tests passed, including fee budgeting, no borrowing, minimum orders, cash-flow-adjusted drawdowns, strict training-label timing, exclusion of future price features and reserved-period perturbation tests.
- Ruff checks pass.
- The end-to-end ridge experiment and CPU/MPS neural experiments complete on a seeded synthetic dataset.
- Intel XPU and Gaudi HPU training were not validated. Quark was inspected read-only; no deployment had been performed at this stage.

## Small-workload timing observation

Each model evaluated 141 monthly intervals after the same initial training history, using eight synthetic assets and a synthetic benchmark. The final 24 return intervals remained reserved. The neural models used 60 full-batch training epochs per refit and the same seed/architecture. Runs were sequential.

| Model | Training device | Experiment runtime | Total fit time |
|---|---|---:|---:|
| Ridge | CPU | 0.031 s | 0.001 s |
| Small neural network | CPU | 0.661 s | 0.623 s |
| Small neural network | Apple MPS GPU | 3.844 s | 3.519 s |

These are single-run smoke measurements, not a controlled hardware benchmark or a prediction of larger-workload performance. Experiment timing excludes interpreter startup and the initial device-availability check; fit timing includes device synchronisation. The same neural allocation decisions resulted on CPU and MPS for this fixture. General numerical parity and repeatability across accelerators are not established.

The fixture has no real financial content. Neither these timings nor the resulting portfolios demonstrate forecasting skill, profitability or a route to £1 million.

The observations support using CPU as the default at this scale. Profile a representative real dataset before adding accelerated scenario simulation or selecting a training accelerator.

## Initial research-pack update (historical)

The subsequent research review added a same-universe equal-weight comparator and a test confirming that it is independent of the model's top-k setting. The suite then contained 21 tests (two required optional PyTorch). Local documentation links, the 24-entry paper register and the development/release branch guards were also checked. At that stage, the C#/.NET architecture was a documented direction; the implementation and validation above followed later.
