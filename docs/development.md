# Development and releases

All development and research use `dev`. `main` is the release branch. Its initial empty commit establishes a common ancestor; it does not represent a product release. The first prototype and research pack are committed on `dev` only.

## Local setup

```sh
git switch dev
git config core.hooksPath .githooks
uv sync --locked
```

Make source, research, dependency, documentation and release-preparation changes on `dev`. Before committing:

```sh
uv run python -m unittest discover -s tests -v
uvx ruff check src tests
git diff --check
git status --short
git add <reviewed-paths>
git commit -m "<describe the final change>"
```

Generated data/runs, environment files, credentials and third-party full-text papers do not belong in Git. Cite papers and retain original review notes. The ignored `data/` and `runs/` folders are not durable backups or tamper-proof research records; arrange appropriate private storage before real research depends on them.

## Checkpoint evidence

Every checkpoint follows the [alignment-first testing process](testing-process.md): preflight before editing, non-vacuous test evidence through `scripts/checkpoint.py`, a real workflow check and an explicit list of what was not verified.

## Release procedure

Release only when there is an actual release decision. On `dev`, finish validation, update package version/release notes, review remaining limitations and commit those changes. Verify the working tree is clean. Then, replacing `X.Y.Z` with the approved version:

```sh
git switch main
TWENTY_RELEASE=1 git merge --no-ff dev -m "release: vX.Y.Z"
git tag -a vX.Y.Z -m "Twenty vX.Y.Z"
git switch dev
git merge --ff-only main
```

No release tag is created for the current research foundation. A software release does not establish trading profitability or authorise live trading. Artifact publication, deployment and broker execution are separate actions.

The hooks reject ordinary commits on `main` and development commits on other branches. The release merge requires `TWENTY_RELEASE=1`. Hooks can be bypassed and do not intercept every Git reference update or fast-forward, so they are a local guardrail, not enforced server policy. Keep release merges explicit and non-fast-forward.

The public Git remote is [Jellman86/twenty](https://github.com/Jellman86/twenty), with `dev` as the default branch. CI scans full Git history and checked-out files for secrets before publishing a tested development image. The local hooks do not establish server-side branch protection; reviewed release changes and protection against direct development pushes to `main` remain release requirements.
