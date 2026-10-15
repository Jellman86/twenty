# Astra review: initial growth lab and purchase simulator

Date: 15 October 2026. Independent GPT-6 Astra high-effort review of the initial application snapshot, including research/execution core, SQLite registry/worker, HTTP transport, endpoints and Svelte components. Reviewer used a temporary copy and made no repository/infrastructure changes.

The reviewer reproduced three P2 findings despite 160 passing tests:

1. Two research symbols mapping to one broker ticker could bypass the per-stock cap. Fixed by rejecting duplicate broker instrument mappings across targets, held and pending exposures.
2. `ResponseHeadersRead` meant `HttpClient.Timeout` did not cover a stalled body. Fixed with a linked operation deadline covering pacing, send and body reads. Timed-out submissions remain unknown, without retry.
3. Planned fees reduced NAV enough to push existing/new exposures above the cap. Fixed with a conservative post-cost NAV floor accounting for fees and a limit above the observed ask. If existing exposure exceeds that floor's cap, the contribution-only planner retains cash and reports the constraint.

Permanent regression tests cover all three, including the existing-holding fee case and a stalled HTTP stream. Synthetic CSV files were also normalised to LF and their shared input hash updated; numerical reference values are unchanged.

Astra found no demonstrated feature/label lookahead defect and confirmed the web holdout gate remained closed. Svelte check/build passed. The reviewer verified the locally bundled macOS SQLite version as 3.53.3; that does not by itself verify Quark's runtime. The UI correctly separates historical choices, synthetic quotes/fills, optional read-only broker access and inactive real orders. Browser and Quark acceptance checks are separate verification.

## Narrow follow-up

Astra re-reviewed the fixes and confirmed all three P2 findings resolved, with no new material defect in the narrow scope. In its temporary copy it passed 21 execution tests, plus two independent checks covering 10,000 seeded plans with mixed GBP/GBX, pending exposure, fees and limit premiums, and stalled summary/pending-order bodies. These validate supplied bounds, not subsequent price moves or genuine broker payloads. No reviewer edits or external network requests were made.
