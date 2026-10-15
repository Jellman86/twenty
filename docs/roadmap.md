# Issue-based roadmap

Track delivery in the [public Twenty project](https://github.com/users/Jellman86/projects/1) and [repository issues](https://github.com/Jellman86/twenty/issues). Development stays on `dev`; `main` is releases only.

- [M1 · Research workbench · #1: Ship the .NET 10 and Svelte 5 research workbench](https://github.com/Jellman86/twenty/issues/1)
- [M1 · Research workbench · #2: Deploy Twenty through Git-backed Dockhand with private HTTPS](https://github.com/Jellman86/twenty/issues/2)
- [M2 · Trustworthy data · #3: Establish dataset provenance and point-in-time schema](https://github.com/Jellman86/twenty/issues/3)
- [M2 · Trustworthy data · #4: Implement a bounded SEC filings collector](https://github.com/Jellman86/twenty/issues/4)
- [M2 · Trustworthy data · #5: Select and validate historical market and FX data](https://github.com/Jellman86/twenty/issues/5)
- [M3 · Evidence and models · #6: Build the decimal accounting and paper backtest reference in C#](https://github.com/Jellman86/twenty/issues/6)
- [M3 · Evidence and models · #7: Create a durable experiment registry and chronological evaluation jobs](https://github.com/Jellman86/twenty/issues/7)
- [M3 · Evidence and models · #8: Train CPU baselines and test filing-change features](https://github.com/Jellman86/twenty/issues/8)
- [M3 · Evidence and models · #9: Benchmark Intel iGPU acceleration on Quark](https://github.com/Jellman86/twenty/issues/9)
- [M4 · Paper portfolio · #10: Implement a persistent paper portfolio and broker capability audit](https://github.com/Jellman86/twenty/issues/10)
- [M4 · Paper portfolio · #11: Define evidence gates and the first release on main](https://github.com/Jellman86/twenty/issues/11)

- [M1 · Research workbench · #12: Keep secrets out of public source and container publication](https://github.com/Jellman86/twenty/issues/12)

## Research planning context

# Research-driven roadmap

15 October 2026. The current state is a privately deployed research workbench, an offline dataset foundation and a local model/backtest harness with synthetic inputs. Papers motivate the roadmap; no entry means that a profitable strategy has been established.

| Priority | Deliverable | Completion evidence | Current state |
|---|---|---|---|
| P0 | Versioned research and development workflow | Critical literature notes, data/protocol decisions, commits on `dev`; releases reserved for `main` | Implemented in this research pack |
| P0 | C#/.NET application foundation | .NET 10 domain/host/tests; shared accounting/timing fixtures against the Python harness | Web host, contribution parity and dataset contracts implemented; portfolio accounting/timing parity remains #6 |
| P0 | Reference simulator and numerical controls | Cash-flow, costs, minimums, future-price and train-label timing tests; fair benchmarks | Implemented for the documented simplified monthly model |
| P0 | Historical-data acceptance | Primary source/contract review; sampled corporate actions/delistings; GBP and timestamp audit | ECB reference-FX wire/units sampled and parsed; stock provider and terminal-return coverage remain unselected |
| P0 | Changing-universe simulation | Historical listing/eligibility, terminal losses/payouts and executable-price handling | Not implemented; blocks credible stock-selection results |
| P1 | Statement/filing archive | Idempotent collection, source hashes, disclosure/availability/revision timestamps and comparable sections | Bounded CLI, originals/cache/quarantine and offline tests implemented; live SEC validation pending (#4) |
| P1 | Formal numeric comparison | Ridge/tree/MLP versus equal-weight, momentum and broad exposure; identical realistic costs and registered inner/outer splits | Ridge/MLP scaffold implemented; historical study and tree model outstanding |
| P1 | Search-aware evaluation | Preserved candidate returns, dependence-aware intervals, disclosed multiple-testing diagnostics and holdout accesses | Local trial registry implemented; statistical controls outstanding |
| P2 | First disclosure-change experiment | Deterministic difference baseline, labelled extraction checks and incremental net-value test | Hypothesis defined; unimplemented |
| P2 | Resource-limited Quark experiment | Isolated environment, verified CPU/XPU capability, bounded RAM/threads and measured runtime/parity | Read-only hardware inspection only |
| P2 | Acceleration | Representative profiling and speedup without altered accounting or evaluation | Local CPU/MPS smoke timings only; GPU backtesting unimplemented |
| P3 | Forward paper evaluation | Frozen candidate, timestamped predictions/orders, operational discrepancy reports | Not started |
| P4 | Broker integration and possible live pilot | Separate review of supported account/instruments, reconciliation, limits, timeout behaviour and live authorisation | Not implemented or authorised by this roadmap |

## First distinctive experiment

Use disclosure changes (P07 in the literature review) as an incremental signal, after trustworthy numeric data exist. Start with deterministic section comparison and a numeric profitability/momentum baseline. Test whether a learned classifier improves extraction, then whether its outputs improve net investment outcomes. Supplier/customer and insider signals remain later hypotheses; adding all of them now would multiply data problems and research choices.

## Compute decision

Quark's inspected Core Ultra 5 235T, Intel Arrow Lake-S iGPU and approximately 32 GB RAM make CPU-first experiments plausible. Intel XPU compatibility remains unverified, and shared-service resource use precludes assuming the whole machine is available. A proposed isolated runner should cap CPU threads, memory, task duration and concurrency; initial candidate limits are four CPU threads, 4 GiB memory and one experiment, to be validated against workload demand before deployment.

Small-model CPU training was faster than Apple MPS in the synthetic smoke workload. That is not a benchmark of Quark's GPU. Add XPU/OpenVINO work only when it serves a measured training/inference bottleneck. A 3,000-stock panel over 300 months contains approximately 900,000 asset-month rows, but the independent time history is still only 300 monthly dates; model selection must account for that dependence.

## Budget and stopping decisions

Keep investments, research expenses and time costs explicit. Do not buy subscriptions or new hardware because a simulator has a promising synthetic result. If a real-data candidate does not improve on simpler alternatives after costs and uncertainty, retain the useful analysis software and decline the trading hypothesis. If good historical data cannot be obtained within an agreed research budget, limit claims to exploratory/forward paper research rather than quietly using a biased surviving-stock panel.

The £1 million aspiration stays in deterministic scenario tools. It is not a release criterion, training reward or justification for leverage/concentration. Any genuine software release is prepared on `dev` and promoted to `main` using the documented release procedure.

## Dataset foundation implementation

Issue #3 has an offline C# schema, validation/selection guards, immutable archive and synthetic leakage fixtures; see [contract v1](data-contract-v1.md). Source collection, vendor coverage and backtest/ledger integration remain separate issues.

## Independent review and FX checkpoint

[Astra review and follow-up](reviews/2026-10-15-astra.md) identified two P1 timing/SEC-path defects, now fixed and re-reviewed without new actionable findings. The [ECB sample audit and parser](ecb-fx.md) advances #5 with explicit reference-rate units and exact-date conversion, while historical availability, complete exchange calendars and stock-data coverage remain open.
