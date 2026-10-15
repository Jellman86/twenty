# Project Twenty

## Branch policy

- All development, documentation and research changes belong on `dev`.
- `main` contains releases only, apart from its empty bootstrap commit. Do not commit development changes to `main` or merge `dev` into `main` as a routine task.
- Keep the working checkout on `dev` after completing work.
- Follow `docs/development.md` for release preparation and the explicit release merge. Do not invent a release merely to finish a task.
- Local hooks in `.githooks` enforce the commit/merge branch convention when enabled with `git config core.hooksPath .githooks`. They are not remote branch protection.

## Research and implementation

- Read `docs/research.md` and the relevant evidence review before changing strategies or model evaluation.
- The intended application base is C#/.NET 10 LTS. Preserve the Python research harness for experiments; follow `docs/architecture.md` rather than extending Python into the whole application by default. The ASP.NET Core/Svelte research workbench is implemented; Python remains the offline experimental harness.
- Prefer primary academic papers, author manuscripts and official data/broker/hardware documentation. Distinguish working papers, published articles and abstract-only reviews.
- Give each research claim a source, limitation and concrete experiment. Do not treat published alpha, long-short returns or synthetic results as achievable ISA returns.
- Keep publication time, information availability and trade execution time separate. Fit preprocessing on training data only. Reserve and record final evaluation use.
- Register all candidate changes, including discarded trials. GPU speed does not justify unlimited unreported searches.
- Preserve the CPU accounting reference. Validate numerical and accounting parity before accelerating simulation.
- Keep datasets, account data, credentials, downloaded papers and generated runs out of Git. Commit original review notes and source metadata instead.
- The user has authorised a research-only Quark web deployment through Git-backed Dockhand. Do not add live trading or deploy training jobs as an incidental consequence of web deployment.

## Validation

```sh
uv run python -m unittest discover -s tests -v
uvx ruff check src/twenty tests/*.py scripts/*.py
dotnet test Twenty.slnx -c Release
npm ci --prefix web
npm run check --prefix web
npm run build --prefix web
```

Use `uv run --extra neural python -m unittest discover -s tests -v` when modifying optional neural training. Core development must remain runnable without PyTorch. Record whether accelerator checks used real devices or mocks, and whether financial inputs were synthetic or historical.
