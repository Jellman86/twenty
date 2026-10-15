# Validation record

## Current foundation validation

The C#/.NET and Svelte workbench is implemented. Dataset foundation commit [`65ac919`](https://github.com/Jellman86/twenty/commit/65ac91986892ff9f7b5119e26e79062fc485141b) adds the [versioned data contract](data-contract-v1.md), point-in-time selectors, immutable archive and offline validation CLI.

- 46 .NET tests pass, covering the existing contribution calculations and the new contracts, availability/revision selection, currency units, provider shapes and archive integrity.
- 22 local Python tests pass with optional PyTorch installed. Core CI runs the same suite with two optional neural tests skipped. Ruff checks pass.
- The CLI accepts and archives the synthetic fixture, rejects invalid inputs and avoids echoing untrusted JSON property names in parse errors.
- [CI run 38764570834](https://github.com/Jellman86/twenty/actions/runs/38764570834) passed .NET, Svelte and Python checks, full-history/file secret scanning with a deliberate fake-secret rejection control, and the container smoke test before image publication.
- The research web container was deployed through Dockhand on Quark and passed the HTTP smoke checks; its metadata reported commit `65ac919` and `brokerConnected: false`.

These checks use synthetic data and an illustrative calendar fixture. They do not establish historical coverage, source availability accuracy, licensing rights, trading profitability or accelerator support. Real provider collection, persistent quarantine and complete exchange calendars remain future work. No broker integration or GPU training job was deployed.

## Initial Python validation (historical)

15 October 2026, local Apple M5 / 16 GB machine, Python 3.12.13, NumPy 2.5.4 and optional PyTorch 2.14.1.

- At this stage, 20 unit/integration tests passed, including fee budgeting, no borrowing, minimum orders, cash-flow-adjusted drawdowns, strict training-label timing, exclusion of future price features and reserved-period perturbation tests.
- Ruff checks pass.
- The end-to-end ridge experiment and CPU/MPS neural experiments complete on a seeded synthetic dataset.
- Intel XPU and Gaudi HPU training were not validated. Quark was inspected read-only; no deployment had been performed at this stage.

## Small-workload timing observation

Each model evaluated 141 monthly intervals after the same initial training history, using eight synthetic assets and a synthetic benchmark. The final 24 return intervals remained reserved. The neural models used 60 full-batch training epochs per refit and the same seed/architecture. Runs were sequential.

| Model | Training device | Experiment runtime | Total fit time |
|---|---|---:|---:|
| Ridge | CPU | 0.031 s | 0.001 s |
| Small neural network | CPU | 0.661 s | 0.623 s |
| Small neural network | Apple MPS GPU | 3.844 s | 3.519 s |

These are single-run smoke measurements, not a controlled hardware benchmark or a prediction of larger-workload performance. Experiment timing excludes interpreter startup and the initial device-availability check; fit timing includes device synchronisation. The same neural allocation decisions resulted on CPU and MPS for this fixture. General numerical parity and repeatability across accelerators are not established.

The fixture has no real financial content. Neither these timings nor the resulting portfolios demonstrate forecasting skill, profitability or a route to £1 million.

The observations support using CPU as the default at this scale. Profile a representative real dataset before adding accelerated scenario simulation or selecting a training accelerator.

## Initial research-pack update (historical)

The subsequent research review added a same-universe equal-weight comparator and a test confirming that it is independent of the model's top-k setting. The suite then contained 21 tests (two required optional PyTorch). Local documentation links, the 24-entry paper register and the development/release branch guards were also checked. At that stage, the C#/.NET architecture was a documented direction; the implementation and validation above followed later.
