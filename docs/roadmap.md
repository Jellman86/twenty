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
| P0 | Historical-data acceptance | Primary source/contract review; sampled corporate actions/delistings; GBP and timestamp audit | ECB reference-FX and AAPL dividend amounts/split independently sampled ([issuer audit](corporate-action-references.md)); listing intervals/audit implemented and converted into genuine v1 identities under the [two-source identity contract](listing-identity.md) (forward-knowable only — no retroactive point-in-time claim), and [price conversion](listing-prices.md) now turns captured histories into raw/total-return bars plus vendor-action observations under the same capture-time availability rule; terminal-return coverage remains open |
| P0 | Changing-universe simulation | Historical listing/eligibility, terminal losses/payouts and executable-price handling | [Changing-universe panel, gap-refusing backtest and trial register](changing-universe-panel.md) implemented; genuine two-source [listing identities](listing-identity.md) and a fixture-validated [price converter](listing-prices.md) provide the input path for the panel end-to-end from captures, and the backtest refuses to buy a name unpriced at its decision; backtests cover zero and simple cash terminal payouts; the panel can value simple stock-merger consideration (`TerminalStock`), and the backtest carries bounded stock-only successor exposure; spinoffs, mixed consideration and suspensions remain unmodelled; a genuine multi-year backtest still awaits both months of accumulated capture vintages with conversion already retaining capture-time availability and audited `RevisionOf` chains, and study/diagnostic integration and executable-price handling remain unimplemented; still blocks credible stock-selection results |
| P1 | Statement/filing archive | Idempotent collection, source hashes, disclosure/availability/revision timestamps and comparable sections | Bounded collector live-validated (Apple, Costco, Microsoft, Baidu, ASML; Apple back to 1999) after genuine-shape fixes and a measured-evidence response cap of 48 MiB (largest collected document 32.3 MiB); documents beyond the cap remain uncollectable (#4) |
| P1 | Formal numeric comparison | Ridge/tree/MLP versus equal-weight, momentum and broad exposure; identical realistic costs and registered inner/outer splits | Ridge/MLP scaffold and deterministic boosted-tree challenger implemented with a declared 12-trial comparison (tree loses to benchmark and ridge on the real panel); historical registered study outstanding |
| P1 | Search-aware evaluation | Preserved candidate returns, dependence-aware intervals, disclosed multiple-testing diagnostics and holdout accesses | Registry, scoped paired bootstrap, registered one-time final studies and family-scoped CSCV-PBO/deflated-Sharpe implemented; whole-programme correction and genuine-data validation outstanding |
| P2 | First disclosure-change experiment | Deterministic difference baseline, labelled extraction checks and incremental net-value test | [Deterministic similarity plus section extraction](filing-change.md) labelled-checked across five genuine issuer styles (Apple modern/legacy, Costco, Microsoft running headers, Baidu conventional 20-F) with exact independent corroboration; ASML designed-report 20-F is a labelled negative (no heading typography); incremental net-value test unimplemented |
| P2 | Resource-limited Quark experiment | Isolated environment, verified CPU/XPU capability, bounded RAM/threads and measured runtime/parity | Done 18 Oct 2026: isolated XPU runtime (no system install), 4-thread CPU bounds, measured parity near-exact; XPU slower at real workload sizes, 2.5–3.7× only from 120k rows |
| P2 | Acceleration | Representative profiling and speedup without altered accounting or evaluation | Measured: no benefit at current panel sizes, so no acceleration adopted; GPU backtesting unimplemented and unjustified |
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

20 October review: price conversion now has a bounded genuine AAPL smoke as well as synthetic tests; inactive coverage and historical universe evidence remain open; bounded [Nasdaq 2015–2026 sessions](nasdaq-sessions.md) are now reviewed, while other venues and intraday halts remain unsupported. Simple stock-only merger settlement now transfers fractional successor exposure without an invented liquidation; successor coverage and corporate-action restrictions remain explicit. See [settlement contract](changing-universe-panel.md#successor-exposure-settlement-20-october-2026). See [review findings](reviews/2026-10-20-post-muse.md).


## Current delivery position (21 October 2026)

The user has set [initial research-v1 acceptance](initial-v1-acceptance.md) as the completion goal, using test-driven development and explicit review of downstream effects. Its eight evidence gates replace conversational percentage estimates. Open external data prerequisites do not count as completed work.

The research workbench and core engineering foundation are operational. The data path now connects immutable captures, two-source listing identities, session-aware prices and registered changing-universe baseline backtests. Those trials now share study reservation checks, prior-trial disclosure and the workspace trial budget; exact requests are retained and the workbench can inspect trial/settlement history and [paired family diagnostics](search-diagnostics.md#changing-universe-families-and-complete-disclosure-20-october-2026). New diagnostics disclose both model and universe registers. [Registered universe selection and one final pair](universe-studies.md) now pass authored API/browser acceptance and [CI/image/Dockhand deployment](universe-study-deployment-2026-10-21.md). Genuine acceptance is bounded: AAPL history and sampled source/filing/FX checks establish specific ingestion behaviours, not a complete investable historical universe. Simple stock-only successor transfers are implemented within the fractional-notional accounting model.

The largest remaining gate is data and evidence, not compute: audited inactive-stock/terminal-outcome coverage, corporate-action adjustment-step checks, executable-price/FX integration and enough honestly available capture vintages. The offline [raw-quantity GBP accounting reference](raw-gbp-ledger.md) now checks fills and labelled FX valuation, while corporate-action settlement and changing-universe integration remain open. Numeric and disclosure-change candidates still need a frozen historical comparison and forward paper evidence. Live execution remains disabled and is a separate gate. A single completion percentage would conceal these dependencies; software foundation is substantially built, investment validation remains early.

The issuer-action checkpoint now corroborates 48 AAPL events. The subsequent [terminal-history checkpoint](terminal-outcomes.md) retains all six previously sampled subjects and finds four post-last-trading rows across AAIC/AAWW/ABMD, two short request windows, an unresolved exact final session and unsupported mixed consideration. A [separately declared ABST request](terminal-outcome-follow-up.md) covers its terminal period and finds one more post-trading row, preserving the earlier 22 rows unchanged. The offline audit correctly fails both records; usable terminal returns remain an open gate. Next evidence work is to resolve those row semantics and independently establish ACC's final session before converting actions. No training or holdout opening is implied by these audits.
