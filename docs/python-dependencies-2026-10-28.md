# Optional Python dependency remediation — 28 October 2026

The push of the recovery-bundle checkpoint surfaced seven open GitHub dependency alerts. Inspection of the actual lock and installed optional environment confirmed `filelock 4.2.0` and `setuptools 84.0.0`, both transitive dependencies of the optional `torch 2.14.1` extra. The core research environment requires NumPy and does not require PyTorch.

The [filelock maintainer advisory](https://github.com/tox-dev/filelock/security/advisories/GHSA-v4x8-3wr5-3wq8) describes a high-severity local filesystem issue in SQLite-backed read/write locks and names 4.2.1 as patched. GitHub also lists five other filelock alerts fixed by that version: GHSA-jqc4-5cvw-7w85, GHSA-9vhv-jxww-xfm5, GHSA-567q-5qvw-pf92, GHSA-2g95-wg33-c5x2 and GHSA-crv4-6mhj-j5jh. The [setuptools maintainer advisory](https://github.com/pypa/setuptools/security/advisories/GHSA-xmj3-9gf7-h52w) describes a Windows legacy-wheel path escape and names 84.1.0 as patched. These are upstream findings; this checkpoint does not claim an exploit was reproduced in Twenty.

## Scope and compatibility contract

The uv lock now selects `filelock 4.2.1` and `setuptools 84.1.0`. Resolver constraints in `pyproject.toml` retain these lower bounds for the repository's uv workflow, without adding either package to the core dependency set. Exactly these two complete package records changed; every other locked package, wheel hash and dependency record, including Torch and NumPy, is identical. No model configuration, C# accounting, report version, research authority, application API/UI or deployed configuration changes.

The pre-change installed-version check failed against the declared patched versions. After locked optional-environment sync, installed metadata matches both patched versions and the unchanged Torch version. All 66 Python tests pass with the neural extra present, including the existing authored CPU-training/workload cases. No accelerator was used and no research trial was registered. A separate fresh core-only environment runs the same 66 tests with three explicit optional-neural skips and confirms Torch is absent. No exploit or Windows-specific runtime check is claimed.

Private preflight, advisory responses, exact lock delta, sync logs, test output and metadata checks are under `/mnt/apps/developer/jellman86/artifacts/twenty/python-security-20261028`. The ordinary `.venv` optional environment was updated; the separate historical XPU benchmark environment and the offline Mac were not changed. No source report was reinterpreted or genuine final evaluation consumed.

GitHub alert closure must be checked after publication/indexing; the patched lock is not evidence that the remote alert UI has already refreshed. CI and image status are likewise separate from completed local checks. This Python-only remediation requires no application deployment.
