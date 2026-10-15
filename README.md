# Twenty research workbench

[Public project roadmap](https://github.com/users/Jellman86/projects/1) · [Issues](https://github.com/Jellman86/twenty/issues) · [Critical literature review](docs/literature-review.md)

C#/.NET 10 + Svelte 5, TypeScript and Vite. A working private growth research lab: native C# model training, speculative strategy backtests, conditional forward scenarios and durable simulated stock purchases. Python remains an independent offline research reference. A durable Trading 212 coordinator is implemented and disabled by default. Private credentials enable read-only capture; explicit account/strategy/quote/wallet evidence and compatible demo validation are required before real execution. See [the initial application](docs/initial-application.md) and [API setup instructions](docs/trading212-setup.md).

```sh
npm ci --prefix web
npm run build --prefix web
dotnet run --project src/Twenty.Api --no-launch-profile --urls http://127.0.0.1:8788
```

Open http://127.0.0.1:8788. For frontend development, run `npm run dev --prefix web` alongside the API; Vite proxies `/api` to port 8788.

Offline dataset validation and point-in-time selection now have a [versioned C# contract](docs/data-contract-v1.md), synthetic fixtures and an immutable archive. Validate the example with `dotnet run --project src/Twenty.Data -- validate fixtures/data/point-in-time.v1.json`. A [bounded SEC filing collector](docs/sec-collector.md) now archives metadata/history and primary reports through an explicit CLI, with offline HTTP tests. Live SEC validation awaits a privately configured monitored contact. The [ECB reference-FX parser and sample audit](docs/ecb-fx.md) are implemented; historical market ingestion and FX availability-policy import remain planned.

CI scans full Git history and checked-out files with checksum-verified Gitleaks before any image publication. Never commit secrets, account data, private `.agents` files or generated datasets. All development belongs on `dev`; `main` is releases only.

# Twenty

Local investment research, starting with a £20 monthly contribution. Twenty trains its own small forecasting models, compares them with simpler strategies and records the evidence. It makes no claim that those contributions will reach £1 million.

The application base is **C#/.NET 10 LTS** with a Svelte 5 workbench and an offline C# dataset validator. Native C# decimal portfolio accounting, CPU ridge training, a SQLite experiment registry and a bounded background worker are implemented. Python remains the independent model/backtest research harness described below. See [the architecture direction](docs/architecture.md).

## What works today

- Browser-run C# experiments: relative-return ranking, momentum, return-index breakout and six-month upside-tail ranking, with explicit full-rebalance or contribution-only allocation. Only the matching contribution-only reports can approve the initial buy-only coordinator.
- Persisted jobs, cancellation, restart recovery, model/report hashes and all attempted trials.
- Paired candidate-family uncertainty diagnostics with joint month resampling, disclosed excluded trials and approximate simultaneous mean-active-return bands; [scope and assumptions](docs/search-diagnostics.md).
- Contribution-only share buy plans, durable simulated partial fills/cancellation and a persisted pause.
- Frozen current-month signal scoring and a separately configured broker coordinator with durable intents, unknown-submission protection and fill reconciliation. See [the execution contract](docs/broker-execution.md).
- GBP contribution/target calculations using annual effective compounding.
- A locally trained ridge regressor, plus an optional small PyTorch neural network.
- Explicit CPU, Apple GPU (`mps`), NVIDIA GPU (`cuda`) or Intel GPU (`xpu`) selection for neural training. `auto` detects CUDA, then XPU, then MPS, then CPU. XPU needs a compatible Intel installation; local implementation checks do not establish support on Quark. HPU is an experimental adapter requiring a separate Intel Gaudi environment; it has not been tested.
- Expanding-window backtests against momentum, same-universe equal weighting and a supplied benchmark, with identical contributions and cost assumptions.
- Five lagged price features, train-only scaling, a strict label-availability gap and 24 reserved final months by default.
- Costs, minimum order notionals, residual cash, fractional GBP allocations and drawdowns measured without treating deposits as investment gains.
- Run manifests, input hashes, model parameters, predictions, equity histories and an append-only experiment register.

**Training and backtesting run through the private web workbench or the offline Python harness. The default deployment has no credentials or broker connection. No real order has been placed by the project. There are no automated deposits or periodic training jobs. The optional execution worker is disabled unless independently configured privately; deployment does not activate it.** The example datasets are synthetic, not historical backtests or evidence of an investment edge. There is no LLM or filing signal in the implemented model yet.

## Run locally

Python 3.12+ and [uv](https://docs.astral.sh/uv/) are required.

```sh
uv sync
uv run twenty project
uv run twenty make-demo
uv run twenty backtest --data data/synthetic.csv
uv run python -m unittest discover -s tests -v
```

Optional neural training:

```sh
uv sync --extra neural
uv run --extra neural twenty hardware --torch
uv run --extra neural twenty backtest --data data/synthetic.csv --model mlp --device cpu
uv run --extra neural twenty backtest --data data/synthetic.csv --model mlp --device mps
```

An explicitly requested unavailable device fails rather than silently switching. GPU execution need not be faster for a small model; measure a representative workload before selecting it. Simulation accounting stays on CPU regardless of the training device. Do not describe this version as GPU-accelerated backtesting.

## Data contract

CSV columns, in this order:

```csv
date,symbol,total_return_index_gbp
2000-01-01,EXAMPLE_BENCHMARK,100
2000-01-01,EXAMPLE_ASSET,100
```

Dates must cover consecutive calendar months, with a single observation date and a complete universe on each date. Include the benchmark. Values are strictly positive GBP total-return indices, not raw share prices or unconverted USD prices. They must consistently incorporate distributions and corporate actions. The simulator holds fractional GBP notionals; it does not infer share quantities from an adjusted index.

Every `prices.csv` requires a `prices.metadata.json` sidecar:

```json
{
  "source": "Provider, dataset, retrieval time and relevant licence",
  "synthetic": false,
  "benchmark": "EXAMPLE_BENCHMARK",
  "currency": "GBP",
  "frequency": "monthly"
}
```

These declarations identify provenance; they do not prove correctness. A fixed set of surviving stocks can create survivorship bias. Missing securities or returns are rejected, not filled with zero. A changing point-in-time universe, bankruptcies and delisting returns need a separate data/simulation extension before real stock-selection results can be trusted. Do not fill historical prices for securities before they existed.

## Timing and evaluation

For a trade/rebalance at monthly observation `t`, features only use prices through `t-1`. The label is the total return from `t` to `t+1`, minus the benchmark return over that interval. At each refit, training labels must end **strictly before** `t`. This conservative monthly lag avoids using a closing price to construct a signal and claiming execution at that same close.

Features are one- and three-month momentum, intermediate momentum skipping the latest available month, trailing volatility and benchmark momentum. Models forecast relative returns; their outputs are ranking scores, not calibrated probabilities or reliable expected profits. Each strategy buys its top-k assets in equal proportions; the benchmark buys the benchmark alone.

The initial training window is 60 available monthly labels, and subsequent refits expand it. Models refit every 12 months by default. Equal weighting holds all non-benchmark assets, independently of model scores and top-k. All comparisons start on the same date and deposit the same amount at the **start** of each simulated monthly interval. The `project` calculator instead uses **month-end** deposits; its output is a separate mathematical scenario.

The last 24 return intervals are excluded from development evaluation. `--evaluate-holdout` evaluates only that reserved interval and records that decision. Viewing its results consumes the holdout for that research programme; do not tune on it and keep calling it unseen. The code records but cannot prevent repeated holdout use or manual inspection of the input.

```sh
uv run twenty backtest --data data/synthetic.csv --evaluate-holdout
```

Results appear in `runs/<run-id>/`. Each run records its configuration before fitting; failed attempts can therefore leave a manifest without a completed report. `runs/experiments.jsonl` records attempts within that output root. Do not delete failed/poor trials during strategy selection or hide trials by changing output roots. The native web workbench has scoped candidate-family bootstrap diagnostics; the Python harness does not. Whole-programme correction, PBO/deflated Sharpe and a preregistered promotion workflow remain outstanding.

## Costs and limits

Default assumptions are 15 basis points FX, 5 basis points half-spread and 5 basis points slippage **per side**, zero purchase tax and a £1 minimum order. These are configurable research assumptions, not a claim that every instrument incurs those costs. GBP-listed instruments may avoid FX; eligible UK shares may incur purchase tax. The initial simulator applies one flat schedule to all instruments, including the benchmark.

```sh
uv run twenty backtest --data data/synthetic.csv --fx-bps 0 --buy-tax-bps 50
```

Sells occur before buys. Buys reserve their transaction costs, and insufficient notionals remain in cash. There is no borrowing or shorting. Cash earns zero. All portfolios start with zero capital at the evaluation start. There are no prior accumulated deposits.

Data/hosting/electricity costs, instrument-specific taxes, foreign withholding, separate fund fees, real order matching and market impact are not implemented. Partial-fill/cancellation accounting is exercised separately with a durable synthetic order simulator, not historical market fills. Fund expenses may already be reflected in a supplied fund total-return series and must not be counted twice. No probability of reaching £1 million, alpha significance or investment recommendation is produced.

## Next milestones

1. Select historical data with corporate-action and point-in-time universe coverage; verify GBP conversions and delisting treatment.
2. Establish price-only model results across market regimes and instrument-specific cost sensitivity tests. Add a gradient-boosted tree challenger and search-aware statistics.
3. Archive SEC/company filings with public availability and first-seen timestamps. Train/evaluate a filing-change feature extractor against simple text-difference baselines; save supporting passages. Historical LLM contamination must be addressed separately.
4. Profile data loading, feature generation, training and simulation independently. Add vectorised scenario batches or compiled accounting only where measurements justify it, retaining a reference simulator for parity checks.
5. Freeze a candidate; collect forward paper predictions and compare them with the same contribution benchmarks.
6. Validate the implemented Trading 212 coordinator against authenticated demo evidence, genuine current signals and independently inspected account/instrument inputs before enabling real execution.

See [the research pack](docs/research.md) for the critical review of 24 academic studies, source/data decisions, evaluation protocol and roadmap.

## Development and releases

All development and research changes belong on **`dev`**. **`main`** is reserved for releases. The current prototype is on `dev`; `main` contains only an empty bootstrap commit, and no release is tagged. Enable the local branch guard with `git config core.hooksPath .githooks`. See [the development/release workflow](docs/development.md).

## Quark target

A read-only inspection on 15 October 2026 found an Intel Core Ultra 5 235T (14 CPU cores), approximately 32 GB installed RAM, Arrow Lake-S Intel Graphics (PCI ID `8086:7d67`) and a render node at `/dev/dri/renderD128`. This is an Intel **GPU**, not a Habana **HPU**. The research web container is now deployed through Git-backed Dockhand. No GPU drivers have been installed for Twenty. Native CPU experiments are explicitly requested and bounded; there is no periodic training, and the default broker remains disconnected.

Start with CPU training/backtests. Verify the Intel compute runtime and PyTorch XPU device detection in an isolated environment before trying neural training on its iGPU. OpenVINO is a separate potential inference backend, not an implemented training backend. Avoid automatic heavyweight jobs: Quark already hosts other services, and the inspection found substantial existing memory/swap use. Benchmark under explicit CPU/memory and concurrency limits without changing its existing workloads.
