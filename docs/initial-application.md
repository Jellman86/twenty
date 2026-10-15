# Initial speculative growth application

Implemented on `dev`, 15 October 2026. The application now performs research in the browser rather than linking only to an offline harness. Its intended question is whether a small, concentrated portfolio can capture unusually large winners after realistic costs and failures. Published wealth concentration motivates studying that question; it does not establish that winners can be identified in advance. See the [critical literature review](literature-review.md) and [evaluation protocol](evaluation-protocol.md).

## Working research loop

The Svelte Growth lab queues a persisted experiment. One bounded .NET hosted worker fits an original five-feature ridge model with train-only preprocessing and expands its chronological training window. It records model snapshots, predictions, trades, all comparators and configuration. Relative-return ranking, lagged momentum, monthly return-index breakout and six-month +50% upside ranking are available hypotheses. The upside model is a ridge fit to binary labels: its score is a ranking, **not a calibrated probability**. It refuses training windows with fewer than five examples of either class; that engineering minimum does not establish statistical adequacy.

Six-month labels are purged so their complete outcome ends strictly before each decision. Features lag the decision by one monthly observation. The final 24 intervals remain excluded; the web API deliberately cannot request a final-holdout run. The Python CLI's separately recorded explicit holdout path still exists. Neither path prevents someone manually inspecting source data.

Every strategy receives identical start-of-month contributions: candidate, momentum, same-universe equal weighting, dataset benchmark and zero-interest cash. Decimal accounting reserves costs, respects minimum notionals and retains cash. The allocation policy can either fully rebalance (selling before buying) or add contributions without selling, with occupied holding slots and the same conservative purchase-cap/cost policy as the initial coordinator. The web form defaults to contribution-only. Equal-target comparators in this mode can drift as holdings appreciate; they are labelled accordingly. Contributions do not repair investment drawdown. The 1/2/4/8-holding button registers four separate trials; it is a research comparison, not an automatic recommendation or an unreported optimiser. Default three holdings and 50% cap are starting hypotheses, not research-established optimal limits.

Forward scenarios resample six-month blocks of the candidate's net development returns with a recorded seed. The plotted 10th/50th/90th path quantiles are conditional scenarios, not calibrated confidence intervals, millionaire probabilities or expected returns. Reusing observed net account returns at a different balance/contribution path also approximates state-dependent minimum-order and cash effects.

## State and operational boundaries

SQLite under `TWENTY_DATA_ROOT` stores datasets, all run attempts, events, paper accounts and the persisted pause. Exact input bytes and SHA-addressed report files accompany it. A process lease prevents two hosts opening the same workspace. Running jobs become interrupted after restart; queued jobs remain queued. Cancelled and failed attempts remain visible. Every completed run binds its configuration hash, input hash, queued commit, actual worker commit, core assembly hash and report hash.

Initial bounds: 8 MiB CSV, 600 months, 100 non-benchmark assets, 20 datasets, 10 active queued/running jobs, 1 worker, 2 minutes per experiment and 1,000 recorded trials. Research report storage is capped at 256 MiB; paper accounts have separate count/history/payload bounds. The image runs with one CPU/512 MiB, no GPU access, a read-only root and a private `/data` volume. Training occurs only for explicitly requested jobs or queued work surviving restart. There is no periodic training or data scrape.

Writes require a same-origin custom request header; cross-site requests are rejected. This is a private home-lab application without multi-user authentication. Keep ingress restricted to trusted LAN/Tailscale. It must not be exposed publicly merely because its source repository is public. Private imported data and credentials never enter Git. The full-history/files CI secret scan remains a prerequisite to image publication.

## Decision and purchase rehearsal

A separate decimal share-quantity planner translates a frozen historical allocation into contribution-only limit buys. It applies verified-style eligibility, currency, price freshness, quantity steps, minima, fee reserves and pending exposure. Existing concentration drift is reported; the initial purchase policy does not sell holdings to repair it.

The durable simulator stores intents before fills, funds each run's account once per simulated month, supports partial cumulative fills/cancellation and survives restarts. Its raw prices are explicitly synthetic and unrelated to return indices. This is an operational rehearsal using historical choices, not a current forward-paper strategy.

An actual Trading 212 HTTP transport exists with fixed origins, bounded responses, paced requests and no POST retry. Optional private credentials enable an explicitly requested read-only account summary. The [subsequent coordinator](broker-execution.md) implements gated order endpoints and an optional private automatic worker. Neither is activated by default; authenticated acceptance remains pending. [Setup, upstream restriction and remaining input contracts](trading212-setup.md).

## Data and evidence still required

The two bundled panels are deterministic synthetic fixtures. An administrator can import an appropriately licensed fixed-universe monthly GBP panel using `--import-panel`; stop the app first because the workspace has one writer. That path is exploratory and does not certify survivorship, delisting returns, corporate-action treatment or historical ISA eligibility. The stricter point-in-time dataset-v1 archive remains a separate ingestion foundation and has not been silently converted into monthly prices.

Useful next experiments need point-in-time small-company universes, failed/delisted companies, raw execution prices and instrument-level costs. Add profitability/disclosure-change features only after availability timing is evidenced. Compare them with price-only/ranking baselines; use a tree challenger and dependence-aware/search-aware uncertainty analysis before selecting a champion. Frozen prospective scoring and authored broker reconciliation tests are now implemented in the separate coordinator; actual current-data and authenticated acceptance remain pending. Intel XPU acceleration remains unverified and unnecessary for this small CPU baseline.

## Validation

Shared Python/C# fixtures check ridge predictions and decimal portfolio accounting. Tests cover future-data perturbation, strict label timing, cash/minima, cancellation/restart, idempotent funding, partial fills and unknown broker responses. `scripts/smoke.py` exercises actual HTTP training, report retrieval and simulated purchase reconciliation; CI runs it inside the bounded, read-only Docker image before publication. Deployment verification is recorded separately after actual Quark testing.
