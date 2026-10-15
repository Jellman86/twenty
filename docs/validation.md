# Initial validation

15 October 2026, local Apple M5 / 16 GB machine, Python 3.12.13, NumPy 2.5.4 and optional PyTorch 2.14.1.

- 20 unit/integration tests pass, including fee budgeting, no borrowing, minimum orders, cash-flow-adjusted drawdowns, strict training-label timing, exclusion of future price features and reserved-period perturbation tests.
- Ruff checks pass.
- The end-to-end ridge experiment and CPU/MPS neural experiments complete on a seeded synthetic dataset.
- Intel XPU and Gaudi HPU training have **not** been validated. Quark was inspected read-only; no deployment was performed.

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

## Research-pack update

The subsequent research review adds a same-universe equal-weight comparator and a test confirming that it is independent of the model's top-k setting. The full suite now contains 21 tests (two require optional PyTorch). Local documentation links, the 24-entry paper register and the development/release branch guards are also checked. The C#/.NET architecture is a documented direction only; no .NET application is implemented or claimed as tested.
