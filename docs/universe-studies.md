# Registered changing-universe studies

The separate [decision-time raw strategy adapter](raw-universe-decisions.md) now derives the same baseline family without a future return period and verifies its weights in raw archive replay. It is not a new registered raw study and does not change this workflow's adjusted-notional unit contract.

Implemented on `dev`, 21 October 2026, extending the 20 October chronological declaration foundation. This is a research workflow over the existing changing-universe momentum baseline, equal weight and cash. Results use local-currency adjusted-return ratios in GBP-labelled notionals; they do not establish achievable GBP wealth, raw share execution or an investment nomination. [Initial-v1 acceptance](initial-v1-acceptance.md) retains those separate requirements.

## Declaration and chronological contract

Before calculation, one SQLite transaction records the immutable plan, complete prior model/universe trial disclosure, all eight development trial IDs and their exact retained requests. The finite family is momentum at 1, 2, 4 and 8 target holdings, with target weights capped at `1 / holdings`. It is fixed without pruning by future eligible counts, observed returns or surviving listings. Fewer qualified names can leave exposure in cash under the existing engine. Every candidate receives base and doubled-variable-cost runs; the minimum order is unchanged. These flat costs are research sensitivities, not verified instrument costs.

For `N` consecutive monthly decision instants:

- The momentum engine needs 12 warm-up periods, including its skipped most recent period. Development evaluates from decision 12 through decision `N-26`, with at least 24 return intervals.
- The latest 24 return intervals are reserved, from decision `N-25` through the final decision `N-1`.
- The full interval between decisions `N-26` and `N-25` is unused for development selection.
- Development receives the prefix ending at `N-26`. Final evaluation receives a separate grid beginning 12 periods before `N-25`, preserving momentum warm-up while starting an empty account at `N-25`. Development holdings and contributions do not transfer. Fractional notional exposures rebalance monthly in both windows.

The smallest declaration has 62 decisions: 24 development intervals, one gap interval and 24 final intervals after warm-up. A 73-decision example has 35 development intervals and 24 final intervals. Dates use the same bounded monthly-grid validation as `ChangingUniversePanel`: consecutive UTC calendar months and 20–45 day spacing. Equivalent offsets are canonicalised to UTC before declaration hashing and request replay. The dataset/code hashes, identifiers, contribution/price-age bounds, dataset creation cutoff and both cost settings must validate.

Synthetic datasets require `engineering` purpose; imported datasets require `research`. The service derives provenance from the hash-verified dataset rather than trusting a client flag. Engineering rehearsals may freeze the best candidate even with a non-positive score solely to exercise the lifecycle; they remain engineering results. Inputs with false synthetic flags in unit guard tests are explicitly authored, not historical investment evidence.

## Complete selection and one final pair

All declared base/stress pairs must succeed under the plan's scientific identity. Failed, cancelled, missing, corrupt or interrupted trials remain in the registry and stop selection; they cannot be replaced by an easier family. Report acceptance checks exact request bytes and semantics, candidate/cost role, dataset/universe/panel identity, evaluation and comparator grids, contributions and ending-value/cost summaries. A rehashed but shortened or configuration-mismatched report is refused.

For each candidate and cost condition, subtract the greater ending notional of equal weight and cash from momentum's ending notional. Maximise the smaller base/stress margin; ordinal candidate ID breaks ties. A non-positive best imported-data score yields `no-candidate` and leaves the final window unspent. There is no external market comparator. The complete eight-stream family also receives joint paired resampling diagnostics with the existing fixed 3/6/12-month settings. Return-based diagnostics do not calibrate the ending-notional screen and cannot correct all prior programme searches.

A frozen choice requires a separate final request with its exact plan SHA and a request UUID. The spent claim, precise UTC window and exactly two final trial IDs are committed before either trial can start. The same UUID returns the original pair/status; another cannot create replacements. Failure, restart, cancellation, input loss and code changes do not reset the claim. The same worst-margin screen evaluates the frozen choice, without re-ranking the alternatives. Imported-data outcomes are `research-complete` or `research-rejected`; synthetic outcomes are `engineering-complete`. None is a broker approval or forward investment nomination.

Prior local work remains disclosed, including failed attempts and other engines. Import/validation necessarily reads the complete file. These operational reservations do not prove source timing or absence of manual inspection, upstream revision, universe bias or other workspaces. A software final window cannot repair unsupported market data.

## Shared exposure, worker and storage controls

Model and universe studies share the 16-study ceiling, 1,000-trial budget, two reserved final slots per developing/frozen study, active genuine reservations and permanent genuine spent claims. New declarations check earlier requested/started work across both engines and dataset aliases. Genuine universe trials with unknown information windows fail closed. Cancellation before a final request releases the active reservation; an already requested final remains spent.

Universe information windows now retain exact UTC start/end instants, including warm-up and price-age lookback, alongside the existing date fields. Exact active reservations and spent windows distinguish an actual shared endpoint from two different times on the same date. Older day-only universe windows use a conservative full-day upper bound. The shared date-only claim table retains an outward-rounded envelope for compatibility; `universe_holdout_windows` retains exact UTC boundaries atomically with it. A missing exact window for a recorded universe final is refused rather than treated as a narrower claim.

One computation lease covers the hosted model worker, registered universe jobs and ordinary synchronous universe calculations. Older queued model work retains admission order; idle backoff releases the lease. Busy ordinary requests are refused before a new trial is admitted, while an existing request's replay remains readable. Jobs use the existing cooperative two-minute cancellation deadline and bounded inputs. Cancellation is checked before a universe report is published. The worker never reruns an interrupted attempt; unstarted declared work can resume only under the same scientific identity.

The 8 MiB input bound, 20 universe-dataset limit, 600-decision cap, 1 MiB study-payload ceiling and existing artifact/workspace checks remain. Payload and trial limits are independent: a large prior snapshot can prevent declaration even below 1,000 trials. A payload refusal occurs before new request files are written or trials committed; no financial calculation occurs. IO failure before admission can leave bounded orphan files, but cannot commit a partial family or spend a final that began no work.

## API and workbench

All writes retain the same-origin/header requirement.

- `GET /api/research/universe-datasets`: bounded imported dataset identity/provenance/universe metadata, without computing returns.
- `POST /api/research/universe-studies`: UUID, name, purpose, dataset SHA, universe, decisions, price age, monthly notional and costs.
- `GET /api/research/universe-studies` and `/{id}`: history/progress and full declaration, prior disclosure, scores, diagnostics and final bindings.
- `GET /api/research/universe-studies/{id}/plan`: exact original plan JSON; `/{id}/events`: lifecycle journal.
- `POST /api/research/universe-studies/{id}/evaluate`: UUID and plan SHA; `/{id}/cancel`: stop future work without deleting evidence.

The workbench selects an already imported captured dataset and universe, accepts month-end 23:00 UTC decisions with a 45-day price age, and explicitly declares the entire family. It shows prior trials, frozen selection, all declared pairs including failures, uncertainty, immutable plan links and the irreversible final request. Request IDs survive ambiguous-response retries within the view; view revisions protect newer selections from late replies. Trial history refreshes after study changes. Final reports remain inspectable but cannot seed a retrospective development-family diagnostic. The capability `changingUniverse.registeredCandidateSelection` is true; `investmentNomination` is false.

## Persistence and acceptance

New additive tables are `universe_studies`, `universe_study_events` and `universe_holdout_windows`; existing claim rows and completed artifacts are retained. Plan bytes are embedded unchanged on state updates, including date-offset escaping. Recovery must retain these tables and use a compatible writer: earlier writers do not understand universe-study reservations, so backward writer compatibility is not claimed. Existing completed model studies remain readable; scientific code changes still stop unfinished studies rather than reinterpreting their plans or reopening final claims.

[Implementation/TDD and downstream review](reviews/2026-10-21-universe-studies.md) records local and authored API/browser acceptance. Raw-price/GBP-FX accounting, inactive-stock/terminal coverage and the final combined capture-to-evaluation acceptance remain open. No new source collection, periodic training, live order or genuine final evaluation was performed to test this workflow.
