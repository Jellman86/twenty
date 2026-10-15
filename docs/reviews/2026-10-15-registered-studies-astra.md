# Astra review: registered studies and final evaluation

Reviewed 15 October 2026 in the existing independent Astra task. The reviewer inspected isolated source snapshots, ran lifecycle tests and independent failure probes, and did not edit repository files, access broker credentials or activate execution.

The design review required atomic registration of every planned candidate/cost pair, reservation before work, a single atomic final claim plus two run IDs, expanding-training semantics, explicit fresh-account versus continuation behavior, a frozen refit schedule and no dropping failed candidates. These are now part of the stored protocol.

## Findings and fixes

- Training-history leakage: checking only requested return-evaluation dates could hide use of an older reserved interval in expanding training. Both prior-access and reservation guards now cover the full feature/training information window from the panel's first observation. A regression uses an older 2008–2010 reserved period with a later 2011 evaluation that still trains through those outcomes.
- Same-close policy selection: development ranking must not use a closing outcome and claim execution at that same close. Studies now end development one monthly interval before the first final decision. Future/final-price perturbations leave development choices unchanged. Ordinary legacy windows retain their explicit prior behavior.
- P2 conflicting study development: disjoint final windows did not prevent a later study's development from consuming an earlier reservation. Admission now checks the new study's full development information window before inserting any jobs; final evaluation and claim recheck other reservations. An independent probe and a regression verify rejection without partial registration.
- P2 claim failure containment: malformed queued data could throw before the worker's old handler, leaving a queued run and faulting the host. Claim now persists a terminal per-run failure and continues valid work; study failures stop the entire declared family. Register-level failures are contained with safe-code logging/backoff. Hosted-worker regressions cover both malformed and oversized inputs while a valid next job completes.

The final follow-up verified both P2 fixes and reported no remaining actionable issue in its focused scope. Twelve lifecycle tests and three independent checks passed in the review snapshot, including a spent final claim that survived failure/restart with unchanged run IDs. The repository subsequently added an oversized-input variant; its complete suite passed 245 .NET tests, with Svelte checking/build, Ruff and secret checks recorded separately before commit/CI/deployment.

The fixed wealth-margin screen nominates only for further research. It is not an optimal-risk result, significance threshold or live approval. Synthetic rehearsals remain non-promotable, and all genuine provenance, manual-inspection, market-data and authenticated acceptance limitations still apply. Final CI/Quark acceptance is recorded in issue 7.
