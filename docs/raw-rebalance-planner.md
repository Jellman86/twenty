# Decision-time raw rebalance planning and quantity orders

24 October 2026. `RawRebalancePlanner` (`raw-rebalance-v1`) now converts **supplied** positive target weights into explicit share orders. `execution-v5` / `raw-execution-v5` accept these orders without inferring a sale's share count from the later opening price. This is an offline bridge toward raw-strategy integration, not a registered candidate-selection workflow, broker adapter or investable backtest.

## Two explicit order sizing modes

`PlannedOrder` retains the existing positive `NotionalMajor` mode: shares are derived from the modelled fill price in the listing's major currency. It now also accepts positive `Quantity` with **zero** `NotionalMajor`. Both fields positive, nonpositive requested shares, or neither sizing instruction refuse as `InvalidOrder`. Existing notional intentions are not reinterpreted.

Quantity mode preserves requested shares across a gap. Selling two held shares after the price falls does not become a request to sell more shares merely to raise the old cash target. The caller's `FillPolicy.QuantityStep` still rounds down; a coarse step can leave a residual position and does not guarantee a complete exit. Minimum local-major notional, gross participation, session/calendar, raw-price, action and pinned-FX checks still apply. Buys and sells in both sizing modes consume the same gross listing/session allowance.

Receipts and refusals retain `RequestedQuantity`; fills also retain actual `Quantity`. The ledger refuses nonpositive requested-share proof or a fill larger than that stated request. Replay independently replans the original order and compares its full receipt. A direct receipt remains a caller assertion about the original request; it is not independently authenticated. Tiny positive share steps use remainder-based flooring, avoiding an overflowing intermediate quotient. Unrepresentable sizing arithmetic produces an explicit order refusal instead of a sizing exception.

## Frozen decision construction

The planner receives a single-owner raw ledger, a point-in-time `DataSlice`, universe ID, positive target weights, future execution deadline, GBP cash reserve, fixed GBP fee per fill and explicit price/FX maximum ages. It makes no ledger or external mutation and emits no order to a broker.

1. Snapshot and validate at most 100 positive targets; weights sum to at most one. Absent holdings have zero target weight. The union of current holdings and targets is bounded at 200 subjects. Invalid/zero weights refuse; use absence to request an exit. Equivalent decision/deadline timezone offsets normalize to UTC, and IDs use ordinal comparison.
2. Require every positive target to be eligible in the supplied decision-time universe. A held listing that lost eligibility may still be sold. The synthetic ISA membership fixture does not establish genuine broker or ISA eligibility.
3. Use ordinary raw GBP valuation with `Executable`-purpose FX. Unresolved actions, unpaid dividends and unsettled terminal claims refuse; receivable estimates never become buying power. Every subject needs consistent active listing units, an available fresh raw close and, when foreign, available fresh local→GBP FX. Known terminal actions cannot be bypassed by stale active metadata.
4. If existing holdings already exactly match the pre-fee targets after the requested cash reserve, emit no trades and reserve no fees. Otherwise reserve **one fee per current/target subject** before sizing. This is a conservative bound, since at most one order is emitted per subject. Unused fee budget remains cash. No iterative fee optimization or no-trade tolerance is introduced.
5. Allocate the remaining equity by the supplied weights. Desired shares are target GBP value divided by known major-unit price and known GBP-per-local-unit FX. Subtract existing shares to form each order; an absent target sells its exact holding. Retain listing, raw bar, FX and source snapshots alongside current/desired shares and target GBP values. Pin each foreign order to that exact **decision-known FX ID**, rather than selecting a later rate after seeing outcomes.
6. Emit sells first, then buys, each in ordinal listing order. Project cash in that actual order using the ledger's multiplication/addition order and one fixed fee per emitted order. A sale must fund its own fee before relying on a later sale. Negative interim cash, inadequate reserve, overflow, underflow to an unrepresentable unit/share target, or a final decimal reserve shortfall refuses explicitly. Input weights and ledger state remain unchanged.

The projected cash is a decision-price estimate. Later gaps, spreads, missing/rejected sales, steps, minimums, volume or stale/superseded FX can refuse execution. No target-weight or future funding guarantee is made, and no order is resized using later prices. Retaining the decision FX pin is a declared modelling convention: it must independently pass the execution-age/availability guards at the later open. A historical `Executable` label does not prove that a broker would quote that rate then. Genuine quote, fee, settlement-cycle and instrument-precision acceptance remains open.

Same-open sale proceeds may fund later same-open purchases in the declared ledger order. This is a model assumption, not evidence of broker settlement availability. Each ledger operation is atomic; a caller executing multiple fills directly does not receive transaction-wide rollback. A complete archive replay uses a fresh private ledger and refuses publication on any failure.

## Authored accounting and timing acceptance

An authored USD example starts with GBP 300, two USD 100 shares bought at GBP 0.8/USD, and GBP 140 cash. At the decision, only listing B is targeted, priced at USD 50. A GBP 10 cash reserve plus two possible GBP 1 fees leaves GBP 288 investable: sell exactly two A shares, then request **7.2 B shares**. Projected cash at decision prices is GBP 10.

At the next declared open A is USD 80 and B is USD 40. Both fills retain the decision FX pin of 0.8; a later available FX rate of 0.9 does not replace it. The sale adds GBP 127 after fee, and the purchase costs GBP 231.40 after fee, leaving **GBP 35.60 cash, 7.2 B shares and GBP 2 fees**. The final USD 40 / 0.9 mark gives **GBP 294.80 equity**; contributions remain GBP 300. This is authored arithmetic, not historical performance.

Tests compare direct execution with schedule compilation, archive execution and fresh verification. A higher opening B price leaves the same frozen 7.2-share request, but fails funding without publishing a partial replay receipt. Perturbed future opens/FX leave the decision output unchanged in the authored fixture. GBP pence targets scale prices once while preserving share quantities.

The original quantity/plan regressions failed before implementation. Additional failing regressions caught fee-induced unnecessary trades, sequential fee funding, underflow and overflowing sizing/step arithmetic before correction. Temporary quantity-sizing, no-op and fee-sequence bypasses must be detected before the restored full suite is accepted. All inputs are authored synthetic; no genuine evaluation, vendor/broker call, optional neural dependency or actual/mocked accelerator is involved. Generated proof is retained privately under `/mnt/apps/developer/jellman86/artifacts/twenty/raw-rebalance-20261024`.

## Boundaries, compatibility and remaining work

The returned immutable plan records the supplied weights, decision evidence, deadline and both decision freshness limits in ticks. It is **not** an admitted research trial, persisted account, frozen strategy family or authenticated data source. The raw schedule/archive binds its own orders, inputs, source code/runtime and output, but does **not** rederive those orders from this plan's original weights or starting ledger. Retaining a separate decision-plan JSON is not that binding. Registered admission must connect these artifacts before claiming a registered raw strategy or complete capture-to-study acceptance.

No HTTP/UI workflow, broker setting, research-store schema, notional report units or strategy candidate family changes. Core changes alter scientific identity and exact raw-archive build identity. Old raw receipts remain immutable and intentionally refuse under a different binary; original exact-build reproduction remains required. Completed registered studies need byte-preservation and final-request compatibility checks before deployment; unfinished reserved studies cannot silently resume with changed science.

Source-wide action discovery/scheduling, genuine settlement and executable FX imports, registered changing-universe raw selection/execution, complete report/paper-account integration and final operational acceptance remain open in [the initial acceptance contract](initial-v1-acceptance.md).

## Local validation and recovery receipts

The restored final implementation passes **1,135 .NET tests** (43 feature cases plus three storage cases added), **45 core Python tests**, Ruff, Svelte checking (zero errors/warnings), the production web build, sensitive-path checks, the scientific-boundary build probe and full-history/files secret scans with the deliberate leak control. Quantity-sizing, fee-induced no-op trading and fee-sequence bypasses were each detected and restored before final validation.

Fresh CLI processes create, reopen, verify and retry the authored funded schedule with byte-identical receipts and unchanged receipt timestamps. The Python Decimal check separately confirms the 7.2-share target, GBP 35.60 cash and GBP 294.80 closing equity. The unfunded opening-gap schedule refuses without publishing a receipt. The separate decision-plan JSON and its SHA-256 are retained; `cli-verification.json` explicitly records `originalDecisionWeightsBoundToReplay: false` and `registeredStudy: false`.

A fresh private compatibility clone under the final changed scientific identity preserves the completed study, exact plan and ten request/report pairs. The original final request returns its prior result, a replacement remains refused, and ten authored trials remain unchanged. Compatibility proof is retained under `/mnt/apps/developer/jellman86/artifacts/twenty/universe-study-cross-version-rebalance-quota-final-20261024`; no production research work or genuine final evaluation was opened. Publication/deployment status is tracked on [issue 6](https://github.com/Jellman86/twenty/issues/6).

## CI-discovered storage race

The first publication attempt failed in the pre-existing asynchronous worker-recovery test, while 1,131 other tests passed. The original assertion did not expose the worker's safe error code. Review separately reproduced a real quota-scan race: a file listed for size accounting can disappear before `FileInfo.Length`, as can SQLite sidecars during concurrent polling. A deterministic regression failed with `FileNotFoundException` on the original scan. This establishes the scan defect; the original CI log alone does not prove its underlying exception was that race.

Quota scanning now treats only a file that vanished after listing as zero stored bytes. Existing files remain counted; enumeration/other I/O errors propagate, and the original 8 MiB per-file and 256 MiB workspace limits remain enforced. Tests pin disappeared-file handling, retained size counting, enumeration failure and both quota limits. The worker test also reports its safe failure code if it fails again. No schema or quota increase is introduced, and deployment remains gated on a passing follow-up CI.

The corrected worker-recovery test also passed five bounded repeat runs (both corruption variants each time) with two-CPU process affinity. The complete 1,135-test suite and scientific compatibility/boundary checks pass after the storage change. These checks use separate authored workspaces; they do not exercise a genuine financial evaluation.
