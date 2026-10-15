# Research basis and model plan

Reviewed 15 October 2026. The initial code is a research foundation; none of the following papers establishes profitable performance for Twenty.

## Research pack

Start with these documents:

- [Critical review of 24 academic studies](literature-review.md): findings, conflicting evidence, relevance to a small ISA, source links and the extent actually reviewed.
- [ECB reference FX audit](ecb-fx.md): real wire-format samples, explicit cross-rate units and an offline XML parser; historical availability import remains unimplemented.
- [SEC filing collector](sec-collector.md): bounded collection, immutable originals, first-observation timing, caching and quarantine; live validation pending.
- [Implemented dataset contract v1](data-contract-v1.md): offline validation, original/revision selection, identity/currency checks and immutable snapshots.
- [Upstream contracts and integration boundaries](upstream-contracts.md): broker request/response semantics, execution gaps, timestamp/FX contracts, terms discrepancy and required fixtures.
- [Data sourcing and collection design](data-sources.md): primary sources, provider candidates, timestamping, delistings, costs and scraper requirements.
- [Model and evaluation protocol](evaluation-protocol.md): limited hypothesis families, chronological tests, benchmarks, search correction and forward validation.
- [Registered contribution studies](registered-studies.md): upfront candidate/cost family, shared development window, frozen selection and one-time final evaluation; synthetic rehearsals remain non-promotable.
- [Prioritised implementation roadmap](roadmap.md): evidence gates and a clear distinction between implemented and planned work.
- [C#/.NET architecture direction](architecture.md): .NET 10 application base, native-model options and optional Python research workers.
- [Development and release workflow](development.md): all work on `dev`, releases on `main`.

The recommendation is to establish trustworthy historical data and small-account accounting first, compare simple numeric models fairly, then test whether filing-change information adds value. Own-model training is feasible; a profitable edge and millionaire outcome are unproven. Modern language-model historical tests need an additional contamination audit.

The review includes opposing evidence on model complexity (Kelly, Malamud & Zhou versus Nagel), volatility management (Moreira & Muir versus Cederburg and colleagues) and factor replication (Hou, Xue & Zhang versus Jensen, Kelly & Pedersen and Chen & Zimmermann). Those disagreements determine experiments rather than being resolved by selecting the most optimistic paper.

The notes below are a compact orientation. The linked review records version/access limits; no faithful replication has been completed. Some latest full-text articles were inaccessible, and their entries are explicitly limited to primary abstracts or excerpts.

## Core evidence

- Gu, Kelly & Xiu (2020), **Empirical Asset Pricing via Machine Learning**, Review of Financial Studies. Nonlinear forecasting motivates tree/neural challengers, while simpler models provide necessary comparisons. [NBER paper and publication details](https://www.nber.org/papers/w25398).
- Jegadeesh & Titman (1993), **Returns to Buying Winners and Selling Losers**, Journal of Finance. Intermediate momentum motivates a non-ML ranking baseline. [Publisher](https://onlinelibrary.wiley.com/doi/10.1111/j.1540-6261.1993.tb04702.x).
- Novy-Marx (2013), **The Other Side of Value: The Gross Profitability Premium**, Journal of Financial Economics. Profitability is a planned fundamental feature, not an implemented feature. [Working paper and publication details](https://www.nber.org/papers/w15940).
- Cohen, Malloy & Nguyen (2020), **Lazy Prices**, Journal of Finance. Changes in corporate disclosures motivate the proposed filing-comparison model. The published long-short strategy is not directly replicable in a long-only ISA. [Publisher](https://onlinelibrary.wiley.com/doi/10.1111/jofi.12885).
- Cohen & Frazzini (2008), **Economic Links and Predictable Returns**, Journal of Finance. Public customer/supplier links motivate a later relationship-based signal. [Author-hosted paper](https://pages.stern.nyu.edu/~afrazzin/pdf/Economic%20Links%20and%20Predictable%20Returns%20-%20Cohen%20and%20Frazzini.pdf).
- McLean & Pontiff (2016), **Does Academic Research Destroy Stock Return Predictability?**, Journal of Finance. Post-publication and out-of-sample decay argue against treating published backtests as attainable forecasts. [Publisher](https://onlinelibrary.wiley.com/doi/10.1111/jofi.12365).
- Bailey, Borwein, López de Prado & Zhu, **The Probability of Backtest Overfitting**. Faster parameter searches need experiment accounting and statistical correction. The initial registry is not itself an overfitting correction. [Author-hosted paper](https://www.davidhbailey.com/dhbpapers/backtest-prob.pdf).
- Lopez-Lira, Tang & Zhu (2025 working paper), **The Memorization Problem: Can We Trust LLMs' Economic Forecasts?** Historical text forecasting with a modern LLM can be contaminated by training data. Train/test timestamps alone do not solve foundation-model memorization. [Preprint](https://arxiv.org/abs/2504.14765).

## What “our own model” means

First train a small supervised model on timestamped market features and realised future relative returns. Compare ridge regression, a tree model and a small neural model under the same evaluation contract. Training a language foundation model from scratch is outside the initial budget and is unnecessary for these hypotheses.

A later text model could classify changes between successive company reports. Maintain human-reviewed labelled examples, document passages, disclosure timestamps and model versions. Test simple text-distance features before embeddings or fine-tuning. A model may extract facts successfully without those facts forecasting returns; measure extraction accuracy and investment value separately.

Retraining should generate a challenger, not automatically replace the deployed model. Use preregistered evaluations and forward evidence. Accelerators change runtime, not the statistical amount of independent market history.

## Acceleration

- PyTorch's MPS backend supports tensor training on compatible Apple GPUs. [Official MPS documentation](https://docs.pytorch.org/docs/stable/mps.html).
- CatBoost's published macOS binaries do not provide CUDA GPU support; NVIDIA acceleration requires a compatible environment. A CPU tree baseline remains worthwhile. [Official platform table](https://catboost.ai/docs/en/installation/cli-installation-binaries).
- Intel Gaudi HPUs require a Gaudi environment and its software stack. Twenty's HPU device branch is experimental and untested; it is not evidence that a particular Gaudi deployment will run. [Official bare-metal setup](https://docs.habana.ai/en/latest/Quick_Start_Guides/Bare_Metal_Quick_Start.html).
- PyTorch's Intel GPU backend is `xpu`, distinct from Gaudi's `hpu`. Supported platform/driver requirements must be checked on the intended machine. Quark's `8086:7d67` is listed as Arrow Lake-S Xe-LPG in Intel's GPU documentation; that alone does not confirm a working PyTorch training runtime. [PyTorch Intel GPU guide](https://docs.pytorch.org/docs/stable/notes/get_start_xpu.html), [Intel GPU hardware table](https://dgpu-docs.intel.com/overview/supported-hardware/i915-driver-gpus.html).
- OpenVINO offers Intel GPU inference; it is a candidate for serving a trained model, not evidence that PyTorch training works on that GPU. [Official GPU inference documentation](https://docs.openvino.ai/2024/openvino-workflow/running-inference/inference-devices-and-modes/gpu-device.html).

Keep money/accounting calculations on the CPU reference implementation. Benchmark feature generation, batched predictions and numerical scenario calculations before porting them. Small tabular models can be faster on CPU because accelerator overhead dominates. Record device, precision, wall time and numerical differences when comparing implementations.

## Primary data and broker references

- [SEC data APIs](https://www.sec.gov/search-filings/edgar-application-programming-interfaces): filings collector implemented and tested with offline fixtures; live validation and Company Facts ingestion remain outstanding.
- [ALFRED vintage data](https://fred.stlouisfed.org/docs/api/fred/alfred.html): use historical releases rather than revised macroeconomic values if macro features are added.
- [Trading 212 API](https://docs.trading212.com/api) and [supported account/order types](https://helpcentre.trading212.com/hc/en-us/articles/14584770928157-Trading-212-API-key): quantity-limit HTTP transport and an isolated order simulator are implemented; real execution is not enabled. See the [current setup/terms audit](trading212-setup.md).
- [Trading 212 ISA fees](https://www.trading212.com/terms/isa) and [HMRC share purchase taxes](https://www.gov.uk/tax-buy-shares): validate actual instrument costs before using any backtest to inform execution.
