# Intel iGPU (XPU) capability and benchmark on Quark — 18 October 2026

Advances [#9](https://github.com/Jellman86/twenty/issues/9). Question: does PyTorch XPU actually work on Quark's Arrow Lake-S iGPU (`8086:7d67`, `/dev/dri/renderD128`), and is Twenty's neural workload faster there than on bounded CPU, at acceptable numerical parity? Answer: **it works, parity is near-exact, and it is still the wrong default for Twenty's actual workload sizes.**

## Isolated runtime (reproducible, no system changes)

The project's `neural` extra installs the CUDA PyTorch build, which reports no XPU device. The benchmark used a separate environment; the project `.venv`, lockfile and deployed container are untouched, and no system packages were installed (sudo was not used):

```sh
uv venv .venv-xpu --python 3.12
uv pip install --python .venv-xpu/bin/python "numpy>=2.2,<3"
uv pip install --python .venv-xpu/bin/python "torch==2.12.1+xpu" \
  --index-url https://download.pytorch.org/whl/xpu \
  --extra-index-url https://pypi.org/simple --index-strategy unsafe-best-match
# Level Zero loader + NEO GPU driver are NOT in the pip bundles (CPU OpenCL only).
# Fedora userland extracted without installing:
dnf download --resolve --destdir=/tmp/xpu-rpms oneapi-level-zero intel-level-zero intel-gmmlib
mkdir .venv-xpu/fedora-root
for rpm in /tmp/xpu-rpms/*.x86_64.rpm; do (cd .venv-xpu/fedora-root && rpm2cpio "$rpm" | cpio -idmu --quiet); done
ldconfig -n .venv-xpu/fedora-root/usr/lib64   # creates the SONAME symlinks extraction skips; without libigc.so.2 kernel builds fail
LD_LIBRARY_PATH=$PWD/.venv-xpu/fedora-root/usr/lib64:$PWD/.venv-xpu/lib .venv-xpu/bin/python scripts/benchmark_accelerator.py --device xpu
```

Capability chain as measured: kernel `i915` bound, render node accessible via the `render` group; extracted `zello_world` initialises the Level Zero driver and finds the GPU; `torch.xpu.is_available()` is true with device "Intel(R) Graphics" (48 EUs, ~29 GB shared memory). Two failure modes worth recording: with the CUDA wheel, XPU reports zero devices; with the XPU wheel but no Level Zero userland, device count is also zero; and with the driver extracted but pre-`ldconfig`, the first kernel build fails (`urProgramBuildExp -> UR_RESULT_ERROR_UNKNOWN`) because IGC cannot be dlopened by SONAME.

## Benchmark design

`scripts/benchmark_accelerator.py` trains Twenty's actual `Neural` MLP (5 features, 32/16 hidden, 60 full-batch epochs, seed 212) on deterministic authored synthetic rows. Bounds declared in the script: 4 CPU threads (the roadmap's candidate cap for shared-host work), at most 5 repeats (3 used, median reported), at most 2M rows. Fit time includes host-to-device transfer and synchronisation; each device ran in its own process for clean peak-RSS. Parity criteria were declared before results: held-out prediction correlation ≥ 0.999 and max absolute difference ≤ 5% of target sd. Both devices used the same isolated build (torch 2.12.1+xpu); the project extra's 2.14 CUDA build was not used, so CPU numbers are internally comparable, not a cross-build claim.

## Results (median of 3 fits; first-fit cost in notes)

| Rows | CPU fit (4 threads) | XPU fit | Speedup | Peak RSS CPU → XPU |
|---|---|---|---|---|
| 6,000 | 0.065 s | 0.247 s | 0.26× | 745 MB → 2,994 MB |
| 120,000 | 1.057 s | 0.425 s | 2.49× | 818 MB → 3,090 MB |
| 900,000 | 10.924 s | 2.927 s | 3.73× | 1,191 MB → 3,090 MB |

Prediction on 4,096 held-out rows: CPU 0.0004 s vs XPU 0.002–0.003 s (launch overhead dominates; irrelevant at Twenty's monthly cadence). Parity passed at every size with room to spare: correlation 1.000000, maximum prediction difference ≤ 1.9e-8 (0.0000 of target sd), held-out MSE identical to six decimals. **First-fit cost is the catch**: the first XPU fit in a process took 208 s (kernel JIT compilation), the next new shape 28 s, warm shapes ~3 s. Medians exclude most of that; a single experiment pays it. Twenty's walk-forward refits change row counts each refit, so shape-specialisation costs can recur inside one run.

## Verdict

- **Capability: validated.** Real kernels (matmul parity 1e-4 abs on 512×512 float32, full MLP training) run on the iGPU from an isolated, reproducible runtime. HPU remains not applicable.
- **Default stays CPU.** Twenty's real training sets are thousands of rows (10 French assets ≈ 2–3k rows; even 100 assets × 250 months is 25k), where XPU is 4× slower before counting JIT warm-up and uses ~3 GB resident. The 2.5–3.7× advantage only appears at ≥120k rows with warm kernels — large synthetic sweeps, not current panels.
- **Accounting is untouched**: simulation/accounting remain CPU decimal reference code regardless of training device, per the architecture rules.
- Nothing is installed system-wide; other Quark services were not disturbed (one bounded process at a time, no container or service changes). A system-level Intel runtime install is an owner/sudo decision and is not needed for this conclusion. Revisit only if a measured workload (for example a declared large synthetic sweep or a future filing-text model) is actually bottlenecked on CPU training.
