# Quark initial application acceptance

Tested 15 October 2026 against the private HTTPS route. No credentials, real account data or broker orders were used. All requests were authored synthetic engineering tests. Development remains on `dev`; no release or merge to `main` was performed.

## Published and deployed artifacts

- Application: `89ba32ace4499b58dc2fbadf6f808434735c9017`.
- [CI run](https://github.com/Jellman86/twenty/actions/runs/38790263272): successful full-history/files secret scan including the fake-secret rejection control, 166 .NET tests, 20 base Python tests (two optional PyTorch tests skipped), Svelte check/build, Ruff and image HTTP smoke.
- Public development image manifest verified directly from GHCR: `sha256:fe23ba143f9ead92b8c22bbd66ebc565e30105f414fbe1e377200006c6d9c1ee`; its `TWENTY_COMMIT` matches the application commit.
- Compose configuration: `bd5bfcb` in the separate docker-configs repository, adding a private named `/data` volume. Its established branch is `main`; Twenty application development remains on `dev`.
- Dockhand resolved the Twenty Git stack, verified source sync to that Compose commit and deployed with repull/forced recreation enabled. Both deploy requests returned success. No Docker lifecycle mutation was performed through the CLI.
- Dockhand reported exactly one running Twenty container, `healthy`, restart count zero, on `ghcr.io/jellman86/twenty:dev`. The app reported the expected commit through HTTPS.

## Verified behavior

The HTTP smoke test trained a native CPU model, retrieved the hash-bound report, checked temporal label boundaries and verified the holdout remained excluded. It created one simulated monthly contribution, reserved share-order cash, applied partial/full fills and proved repeated funding/reconciliation requests did not duplicate balances or holdings. Input/origin rejection also passed.

A second simulated monthly contribution was left partially filled and the durable pause enabled. Dockhand then forcibly recreated the container. Previously completed run records, exact report contents, account holdings, decisions, partial fills, funding history and pause state were preserved. Replaying the prior funding request returned the existing account. Cancelling the unfilled remainder released reserves while retaining the filled shares and cash debits; contributions remained exactly £40. The pause was then restored to the unpaused state.

All four hypotheses (relative-return ridge, momentum, breakout and six-month upside-tail) subsequently completed through the deployed HTTP API. A queued experiment was cancelled on Quark and remained recorded as cancelled. The deployed browser rendered the growth lab and accepted an explicitly requested CPU training job. Local browser acceptance also inspected report comparisons and the decision/ledger page; an order-form layout defect found there was fixed before deployment.

Local Python validation additionally passed all 22 tests with PyTorch available, including CPU neural training; no XPU device was used.

These checks cover initial operations under the one-CPU/512-MiB container bounds. They are not historical-market validation, a long prospective paper period, GPU/XPU compatibility testing or genuine Trading 212 contract verification. No periodic training or trading schedule is enabled.
