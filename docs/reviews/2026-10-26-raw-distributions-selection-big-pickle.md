# Raw distributions and selection review

OpenCode Big Pickle independently reviewed `d6ec973..f09c358` read-only on 26 October 2026. Scope included registered per-share distribution policies, ownership, payment/FX allocation, replay, selection provenance, worker recovery and reservations. It inspected the ledger/scheduler dependencies, independently recomputed dividend/zero rehearsal balances and selection scores, and ran the 65 then-current `RawStudy` tests. It reported no High/Medium findings and two Low findings.

## Report boundary wording — accepted

The frozen v3 report still denied selection while the separate development-selection endpoint was implemented. The limitation now explicitly calls development selection a separate recorded step and retains the exclusions for final evaluation, investment nomination and broker execution. A regression failed against the original wording; the exact limitation and forged-limitation recovery guards are tested. No production v3 raw reports existed when this wording was corrected; earlier private rehearsal artifacts remain preserved under their original scientific identity.

## Post-window action evidence — not reproduced

The review inferred that an action effective during development but disclosed after its end would reach the ledger without a catalog pin and fail late. `DataSlice.EffectiveActions` does filter on effectiveness, but its observations already come from `PointInTimeDataset.AsOf`, which excludes evidence unavailable at the operation's decision time. Requiring such future disclosures in the development catalog would change that information boundary.

Three authored regressions place a split, dividend or zero delisting inside development with availability one day after development ends. They retain unchanged frozen raw prices to isolate evidence filtering, rather than assert historical economics. Each confirms absence at the development cutoff and presence after disclosure, executes with an empty catalog and actual invested holdings, compares every account/month with the no-action baseline, and verifies the archived report by reconstruction/replay. All three pass without changing the production filter. This does not establish late-disclosure handling for genuine data: actions disclosed inside the window still require pins and retain the history/unsupported-action refusal guards.

## Limits

The review and regressions use authored engineering datasets and private rehearsal artifacts. No genuine holdout, vendor/broker request or production experiment was used. This review does not complete source-wide action discovery, historical availability, pending-claim valuation across decisions, final evaluation or operational/paper acceptance.
