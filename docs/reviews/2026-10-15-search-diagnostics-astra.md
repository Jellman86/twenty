# Astra review: candidate-family diagnostics

Reviewed 15 October 2026 by the existing independent Astra review task. The reviewer inspected an isolated copy, ran focused tests and inspected the synthetic local UI. No repository edits, broker credentials, authenticated requests or orders were made by the reviewer.

Scope: statistical construction/claims, complete local trial-family selection, calendar alignment and leakage, artifact integrity, bounds, persistence, cancellation/restart, and UI provenance. The shared geometric-block sampling and maximum absolute centred-mean band matched the stated retrospective method. The result is scoped to included recorded development returns; it does not open reserved data or approve execution.

## Findings and resolution

- Capacity headroom: saving a large register snapshot near the 32 MiB journal cap could fail, then its equally large failure record could fail too, leaving a running attempt. Admission now requires room for the maximum 1 MiB permitted payload while the single diagnostic worker owns that headroom. A regression fills a valid hashed synthetic journal near capacity and checks that admission rejects without leaving a running row. Astra independently reproduced the original problem and verified the admission fix.
- P3 expected-block label: `months / expectedBlockMonths` was a block-equivalent proxy. The sampler's expected block starts are `1 + (months − 1) / expectedBlockMonths`. The calculation and UI now use that quantity; the UI explicitly says it is not an independent sample count. A constant-paired-return test also verifies the formula. This correction changes no uncertainty band.

The completed review reported no remaining P1/P2 findings. Astra's run passed 13 focused tests plus Svelte check/build and synthetic UI flow. The repository's full suite separately passed 232 .NET tests after both corrections; subsequent CI and deployment evidence are recorded in issue 7. Fixed-family stationarity, short-history, missing-search and data-quality limitations remain material. No statistical profitability or broker acceptance is inferred from these tests.
