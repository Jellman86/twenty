# Changing-universe study declaration foundation

20 October 2026. `UniverseStudyProtocol.Declare` is an internal engineering foundation for [V1-04](initial-v1-acceptance.md), not an exposed registered-study workflow. It currently produces a pure declaration from dataset/code identity metadata, provenance, requested monthly decision times, price-age policy, contribution and costs. It does not load a dataset, build returns, admit trials, reserve outcomes, freeze a winner or authorise a final evaluation. The API capability `registeredCandidateSelection` remains false.

## Chronological contract

For `N` consecutive monthly decision instants:

- The momentum engine needs 12 warm-up periods, including its skipped most recent period. Development evaluates from decision 12 through decision `N-26`, with at least 24 return intervals.
- The latest 24 return intervals are reserved, from decision `N-25` through the final decision `N-1`.
- The full interval between decisions `N-26` and `N-25` is unused for development selection. A result ending at one close cannot select a purchase at that same close.
- Development receives the prefix ending at `N-26`. Final rehearsal receives a separate grid beginning 12 periods before `N-25`, preserving momentum warm-up while starting its account empty at `N-25`. No development holdings or contributions transfer into the final account.

The smallest valid declaration has 62 decisions: 24 development intervals, one gap interval and 24 final intervals after the initial warm-up. A 73-decision authored example has 35 development intervals and 24 final intervals. Dates use the exact same bounded monthly-grid validation as `ChangingUniversePanel`, including consecutive UTC calendar months and 20–45 day spacing. Equivalent offset representations are recorded canonically in UTC. Hashes, identifiers, contribution/price-age bounds and both cost settings must validate before a declaration is returned.

The finite family is the existing momentum baseline at 1, 2, 4 and 8 target holdings with purchase targets capped at `1 / holdings`. It is fixed without inspecting future eligible counts, returns or surviving names; an insufficient eligible set is handled by the existing engine rather than used to prune the candidate family after inspection. Base costs are declared; stress doubles the FX/spread/slippage/tax basis-point assumptions while retaining the minimum order. This is an engineering sensitivity, not a claim about actual instrument costs. Synthetic metadata requires `engineering` purpose; imported-data metadata requires `research`. A future service must derive that flag from the hash-verified dataset, never trust it from a client.

The plan explicitly records that existing universe results use local-currency adjusted-return ratios in GBP notionals, **not achievable GBP wealth**. This foundation does not close raw execution/GBP-FX accounting, terminal-data acceptance or investment nomination gates.

## Tests and outstanding integration

Six first-pass declaration cases failed against an explicit unimplemented stub before the planner was added. Fifteen authored .NET cases now cover exact development/gap/final boundaries, minimum evidence length, family/cost declarations, provenance purpose, canonical offsets and invalid identity/grid/execution assumptions. A separate dataset-based check perturbs every price after the reserved boundary and confirms an identical development report; the final rehearsal has exactly 24 intervals and starts each comparator with one £20 notional contribution, ending with £480 contributed. This uses authored financial inputs and already inspectable synthetic final periods; no genuine final window is read.

Still required before the capability can become true: atomic declaration/all-trial admission and prior-register snapshot; shared cross-engine reservations, capacity and one-worker scheduling; exact plan/request/report integrity; failure/cancellation/restart without replacement trials; complete base/stress family scoring and diagnostics; frozen selection; one irreversible, idempotent final pair recorded before computation; and UI/recovery acceptance. A pure plan or passing rehearsal cannot stand in for those controls. Declaration alone consumes no trial slot and is not a preregistration record.

The added Core source changes scientific assembly identity. Existing completed artifacts remain readable and byte-preserved; existing unfinished studies must retain their code-change refusal. No study is automatically re-registered, no holdout claim is reset, and no new source collection, training job or broker operation is scheduled.
