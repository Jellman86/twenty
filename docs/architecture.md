# Architecture direction: C#/.NET + Svelte application, Python research

Decision date: 15 October 2026. Based on the user's preference for C#/.NET. **Recommended application foundation: .NET 10 LTS.** The first implemented slice is an ASP.NET Core application serving a Svelte 5 + TypeScript + Vite dashboard, alongside the Python research harness. It now exposes native CPU strategy experiments, decimal notional backtests, conditional scenarios and persistent simulated share portfolios alongside contribution/evidence navigation. There is no certified historical dataset or live trading. See [the implemented application](initial-application.md).

The native boundary now has three libraries/hosts: `Twenty.Core` owns data contracts, bounded readers, modelling and accounting; `Twenty.Research` owns persisted studies, workers, diagnostics, research service composition and HTTP admission; `Twenty.Api` hosts those routes and the optional broker coordinator. Research has no reference to the broker host. Scientific study identity covers Core and Research, including the embedded dependency lock and build SDK; execution evidence additionally covers the API and runtime/architecture. This allows an adapter-only fix without reopening a scientific holdout. See [versioned study identity](registered-studies.md#versioned-code-identity).

Microsoft lists .NET 10 as an active LTS release supported until November 2028. The local machine already has the .NET 10.0.302 SDK. At implementation, pin an appropriate supported SDK and keep servicing updates current rather than selecting a preview. [Official support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Component responsibilities

| Component | Initial technology | Responsibility |
|---|---|---|
| Portfolio/research domain | C# class library | Money, contributions, constraints, allocation and reference backtesting |
| Application host | ASP.NET Core with hosted workers | Local/private API, job lifecycle, cancellation, collection and experiment orchestration |
| Structured state | SQLite initially | Dataset/run metadata, ingestion cursors, model registry and later paper-account ledger |
| Original/large data | Immutable files; columnar format where justified | Filing originals, versioned datasets, feature snapshots and model artifacts |
| First native trained models | Five-feature native ridge; ML.NET tree candidate | CPU regression/tree baselines under Twenty's chronological evaluation contract |
| Neural/text experiments | Existing Python environment, optional worker | PyTorch and specialised research libraries, including prospective Intel XPU tests |
| Exported-model inference | ONNX Runtime C# where export is supported | Score compatible frozen models without a Python process |
| Dashboard | Svelte 5 + TypeScript + Vite | Private research workbench; ASP.NET serves its compiled static assets |
| Tests | .NET tests plus existing Python tests | Accounting/timing invariants and shared cross-language fixtures |

Begin with a domain library, application host and tests in one repository, rather than distributing a small system across multiple network services. A Python training job can be a bounded subprocess with versioned file inputs/outputs; it does not initially need an HTTP service. Keep schema contracts independent of a particular framework.

ASP.NET Core supports hosted/background workers, but durable job state, concurrency limits and cancellation behaviour must be implemented explicitly. Long training jobs must not block API startup or hold the money/accounting lock. [Official hosted-services documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0).

## Training our own models

An all-C# first model is viable: ML.NET exposes regression and ranking tree trainers, including LightGBM. Native runtime/OS/architecture dependencies still need verification on both the development Mac and Quark. Select a CPU trainer first and apply the same train-only preprocessing, chronological folds and cost model as the Python harness. Do not use a random train/test split just because an API example does. [ML.NET trainer API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.lightgbmextensions.lightgbm?view=ml-dotnet-4.0), [official repository](https://github.com/dotnet/machinelearning).

Keep Python available for experiments where it materially improves library access, especially neural/text models and Intel GPU training. That does not move portfolio rules or broker authority into a notebook. Model workers output predictions/artifacts; the C# domain owns allocations, budgets and eventual order constraints.

ONNX Runtime provides C# inference bindings, but export compatibility is model/operator dependent. The current JSON model snapshots are not ONNX exports. A conversion stage needs preprocessing metadata, feature ordering, input shapes and prediction-parity tests. Do not assume every model or fitted preprocessing pipeline exports faithfully. [Official C# API](https://onnxruntime.ai/docs/get-started/with-csharp).

## Quark and Intel GPU

The Intel PyTorch backend is `xpu`; Intel Gaudi's `hpu` is unrelated to Quark's integrated graphics. Establish a working isolated Python XPU runtime before choosing GPU neural training. The existing device flag is not compatibility evidence. [PyTorch Intel GPU guide](https://docs.pytorch.org/docs/stable/notes/get_start_xpu.html).

OpenVINO is a possible inference accelerator, not the required application base. ONNX Runtime's OpenVINO documentation describes a custom NuGet build for C# integration, so this is not promised as a one-package switch. CPU inference is the initial portable path; add OpenVINO only after profiling and numerical parity tests. TorchSharp is not selected for Intel acceleration without a separate backend compatibility proof. [Official ONNX/OpenVINO provider documentation](https://onnxruntime.ai/docs/reference/execution-providers/OpenVINO-ExecutionProvider.html).

Backtesting is an application calculation, not automatically a neural GPU workload. Implement the C# reference with explicit currency precision and deterministic fixtures, then profile independent scenarios/feature calculations before optimising. Use decimal amounts where appropriate for cash/order accounting and documented floating-point arithmetic for statistical arrays; conversion, rounding and minimum checks require tests.

## Migration sequence

1. Preserve the working Python research harness and its versioned tests.
2. Add the .NET domain/host/test solution on `dev`; port contribution scenarios and accounting with shared golden fixtures before adding providers.
3. Establish SQLite metadata and immutable dataset contracts; implement collection/timestamp audits.
4. Add the native CPU model challenger and/or a bounded Python training adapter. Keep research comparisons model- and language-independent.
5. Add validated ONNX inference and extend the Svelte dashboard with real registry and paper-ledger results.
6. The research dashboard can run privately on Quark now. Add a broker adapter only after evidence/engineering gates and separate execution authorisation.

The critical literature and data requirements do not change with programming language. No profitability claim, dataset purchase, remote deployment or main-branch release follows from this architecture choice.

## External contracts

Implement adapters against the [upstream contract audit](upstream-contracts.md). In particular, the broker accepts quantity orders rather than cash budgets and explicitly warns that order-creation retries can duplicate orders. The existing cash-notional harness is an idealised research reference. A future execution ledger needs quantity precision, cost reserves, partial-fill accounting and unknown-submission reconciliation. The linked terms also explicitly restrict Algorithmic Trading. Development continues under the user’s instruction; [the setup audit](trading212-setup.md) records the restriction and precise technical inputs still missing.

## Broker coordinator checkpoint

The [account-bound coordinator](broker-execution.md) now implements frozen current-month signals, an independent broker sleeve, durable pre-submit markers, history/fill reconciliation, private exact evidence capture and code/contract-bound demo receipts. Its optional worker remains disabled without explicit private configuration; Quark is not activated for broker trading. Authenticated payload/quote/account acceptance is still external work.
