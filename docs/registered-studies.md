# Registered contribution studies

Implemented on `dev`, 15 October 2026. The Growth lab can declare a finite policy comparison, run its entire candidate family, freeze a choice and request one fixed final evaluation. This is a research decision workflow. No synthetic result, positive wealth margin or historical final screen authorises live trading.

## Declared first study

Before any job starts, one SQLite transaction records the immutable plan, all development run IDs and a registration event. The family is the four implemented hypotheses—relative-return ridge, momentum, return-index breakout and six-month upside-tail ridge—crossed with feasible 1, 2, 4 and 8 holdings. Equal purchase targets are `1 / holdings`. A panel with eight assets has 16 candidates and 32 base/stress development runs. These concentrations are research hypotheses; no paper establishes that one is optimal for this account.

The UI uses £20/month. The API can declare another validated contribution before work starts; it cannot change one inside an existing study. Base costs remain the explicitly declared flat research schedule. Stress doubles FX, spread, slippage and purchase-tax rates, retaining the same minimum order. This is a sensitivity, not a worst-case bound or a substitute for verified per-instrument costs.

Every candidate has 60 initial training months, expanding subsequent training, annual refits, regularisation 10 and contribution-only holdings. All development accounts start empty on the same date, aligned to the six-month-label hypothesis. The latest 24 return intervals are reserved. Development ends **one monthly interval before** the first final decision, leaving a selection/execution gap. The family therefore cannot be ranked using a closing outcome and claim a first final purchase at that same close. Training labels still end strictly before every decision; preprocessing fits only the training data.

Final evaluation also starts with an empty account and identical £20 deposits across its comparators. It tests starting a new contribution sleeve in the reserved regime, **not** continuing the development portfolio and occupied holdings. Refits occur at the final-window start and annually thereafter, using only then-available past labels. The policy parameters and schedule are frozen; coefficients can refit according to that declared online policy. There is no final-period hyperparameter or candidate reselection.

## Selection rule and uncertainty

All declared base/stress pairs must succeed under the same code. A cancelled, failed or interrupted candidate stops the study rather than disappearing from a shortlist. Planned jobs count in the global register before they run; the existing worker processes them one at a time. The ordinary active-job cap is not expanded into 32 parallel jobs.

For each cost setting, compute candidate ending GBP minus the greatest ending GBP among the supplied benchmark, all-asset equal targets and zero-interest cash. The candidate score is the smaller of its base and stress margins. Maximise that score, with ordinal candidate ID breaking ties. Identical dates and deposits make this an account-wealth comparison. Momentum remains reported, including the fact that a momentum candidate cannot outperform its identical own momentum comparator. A positive imported-data score nominates only for final research; it is not a significance threshold. A non-positive best score leaves the final window unspent and produces no candidate.

The study also retains paired stationary-bootstrap diagnostics across all base/stress return streams and comparators, using the fixed 3/6/12-month settings described in [search diagnostics](search-diagnostics.md). Earlier local trial IDs, configurations, status and report hashes are disclosed. The plan is declared before **this** family's work; that does not make the entire project preregistered or erase earlier searches. Manual inspection and other workspaces cannot be certified absent. Confidence bands are conditional approximations and do not automatically approve selection.

This shape follows the project [evaluation protocol](evaluation-protocol.md). The [PBO author manuscript](https://www.davidhbailey.com/dhbpapers/backtest-prob.pdf) motivates accounting for selection over alternatives; this software does not implement its CSCV statistic. A final holdout, registration record or bootstrap diagnostic cannot repair biased universes, unreported searches, revised data or future-information leakage.

## Reserved-data controls

For imported research, registration checks previously requested/started local runs across **all non-synthetic dataset identities**, including metadata aliases. The information window begins at the panel's first observation, covering expanding training and features even when evaluation starts later. A failed/started run is conservatively treated as potentially having accessed its requested window. Known queued/planned work is checked too. Shared boundary dates alone do not constitute a shared subsequent return outcome.

An active genuine study reserves its final calendar outcomes. Ordinary experiments and new studies cannot fit/evaluate through them. A new study's **development information window** is checked against existing reservations, as well as its final interval. Final claims are permanently recorded; overlapping spent outcomes cannot become unseen again by changing a dataset ID, candidate, cost setting or request UUID. These are conservative workspace controls, not proof of upstream timing or absence of manual inspection. File import/validation necessarily reads the panel; the controls concern use in model fitting, selection and performance evaluation. The monthly fixed-universe contract still needs a genuine point-in-time coverage audit.

Synthetic inputs must use immutable `engineering` purpose. Rehearsals explicitly use already inspectable authored data, may freeze the highest-scoring policy even if its margin is negative solely to exercise the final plumbing, and always end as `engineering-complete`, never a real investment nomination. Each rehearsal still has one final pair and no replacement. Repeating an engineering study is another disclosed software test, not fresh unseen evidence. Imported data cannot request engineering purpose, and synthetic data cannot request research purpose.

## One final request

`Evaluate the frozen candidate` records the request UUID, plan binding, spent interval and exactly two final run IDs in the same transaction before either can start. Both base/stress conditions were declared upfront; they are one final decision, not two attempts to find a winner. The request is irreversible within the app. Reusing its UUID returns the original pair/result. A different UUID cannot create replacements. Failed, cancelled, interrupted or code-mismatched work retains the claim. A restart can continue unstarted declared work under identical code, but never replays an interrupted running trial.

The final decision applies the same worst-margin screen. A negative/non-positive result is `rejected`: the frozen choice remains recorded, and the application cannot substitute a runner-up after observing final data. A positive imported-data result is `forward-candidate`, requiring new prospective validation and independent evidence; it is not live approval. Synthetic outcomes always remain engineering results. Plans/code changes stop unfinished work; original plan JSON, hashes and completed historical evidence remain readable.

The optional broker coordinator accepts a study-bound policy only for that study's selected completed **final-base** report, with the matching study code and appropriate status. Stress, unselected, failed and synthetic-live study paths are rejected. Existing private account/ISA, data, quote/rule, wallet, strategy evidence and genuine-demo requirements still apply. Standalone externally reviewed strategy evidence remains a separate explicit private configuration path; this workflow does not silently create or replace it. No credentials or actual orders are created by study registration/evaluation.

## Versioned code identity

New plans use `research-assemblies-v1`: an ordinal, JSON-framed manifest of the actual Core and Research DLL SHA-256 values. Research includes the worker, bounded input readers through Core, lifecycle, admission rules, service composition and routes. Its compiled resources include the exact dependency lock and build SDK stamp. Repository-wide revision metadata and Source Link are disabled in these two scientific assemblies; the host still records the deployed commit separately. API-only source changes therefore preserve scientific identity, while model, accounting, reader, research or toolchain changes invalidate it. CI compiles both cases and two different revision stamps without opening a holdout.

Plans also record the actual framework and architecture. They are provenance, not enforced scientific compatibility checks or a numerical-parity certificate. A runtime/architecture migration still needs independent CPU/accounting validation; matching artifact hashes alone do not establish identical floating-point outcomes. Execution evidence is more conservative: `execution-assemblies-v2` covers Core, Research, API and the exact runtime/architecture, requiring fresh compatible genuine demo evidence after any of those change.

Plans lacking a scheme remain `legacy-core-api-assemblies-v1`. Original plan bytes/hashes and completed results remain readable; unfinished legacy work cannot borrow the new fingerprint. No plan is relabelled, no spent interval is reopened and a repeated final request returns its original pair. Actual research changes still stop unfinished studies rather than retroactively reinterpreting results.

## API, persistence and bounds

- `POST /api/research/studies`: request UUID, dataset ID, name, purpose, contribution and base costs. Returns the registered plan; request reuse is idempotent and conflicting reuse fails.
- `GET /api/research/studies`: bounded summaries; `GET /api/research/studies/{id}`: full declaration, prior trials, choice, diagnostics and final bindings.
- `GET /api/research/studies/{id}/plan`: exact immutable JSON bytes for independent SHA-256 verification, preserved when DTOs evolve.
- `GET /api/research/studies/{id}/events`: registration, selection, pre-work spend and terminal events; individual run events remain in the registry.
- `POST /api/research/studies/{id}/evaluate`: request UUID and exact plan SHA; `POST /api/research/studies/{id}/cancel`: stops future work, requests running-job cancellation and retains every final claim.

The existing same-origin header applies to writes. Sixteen studies maximum, 1 MiB per study payload, the existing 1,000-trial limit and 256 MiB pre-report workspace check, one worker and two minutes per model job remain enforced. Two final-trial slots are reserved while a study develops or awaits its final request. A bounded two-minute diagnostic calculation follows the development jobs. No periodic retraining, scraping, deposit or broker action is scheduled.

True historical data, failures/delistings, instrument costs, availability timing, prospective model evaluation and authenticated broker acceptance remain external or separate unfinished gates. The source repository contains original code/review notes and authored unit fixtures, never account credentials or private imported datasets.
