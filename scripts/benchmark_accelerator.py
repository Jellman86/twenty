"""Bounded CPU vs Intel XPU benchmark for Twenty's own Neural workload (issue #9).

The workload is the exact ``twenty.models.Neural`` MLP (5 features, 32/16 hidden,
60 full-batch epochs, seed 212) trained on deterministic authored synthetic rows.
It measures end-to-end fit time (including host-to-device transfer inside
``Neural.fit``), prediction time, peak resident memory of the process, and
CPU/XPU numerical parity on held-out rows from the same authored generator.

This is an engineering measurement, not evidence about markets or models.
Synthetic speed must not be quoted as expected research throughput on real
panels, and CPU remains the default device unless a run of this script on the
target host shows a measured end-to-end benefit at acceptable parity.

Declared bounds: CPU threads are capped (default 4, the roadmap's candidate
limit for shared Quark experiments), row counts are capped, no network or
dataset files are read, and each fit is a single bounded job. Run each device
in its own process for uncontaminated peak-memory figures.

Parity criteria, declared before results: prediction Pearson correlation at
least 0.999 and maximum absolute prediction difference at most 5% of the
target standard deviation between devices at the same seed. Both are reported
whether or not they pass; float32 device arithmetic is not expected to be
bit-identical.

Usage (isolated environment; see docs/quark-igpu-benchmark-2026-10-18.md):
    LD_LIBRARY_PATH=<level-zero-userland>/usr/lib64:.venv-xpu/lib \
        .venv-xpu/bin/python scripts/benchmark_accelerator.py --device xpu
    .venv-xpu/bin/python scripts/benchmark_accelerator.py --device cpu
Exit code 3 means XPU was requested but is unavailable; the JSON still prints.
"""

from __future__ import annotations

import argparse
import json
import platform
import resource
import statistics
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

SEED = 212
MAX_ROWS = 2_000_000
PARITY_MIN_CORRELATION = 0.999
PARITY_MAX_DIFF_FRACTION = 0.05


def synthetic(rows: int, seed: int = SEED) -> tuple[np.ndarray, np.ndarray]:
    """Authored five-feature rows with a linear-plus-interaction target. Not market data."""
    if rows < 2 or rows > MAX_ROWS:
        raise ValueError("rows out of declared bounds")
    rng = np.random.default_rng(seed)
    x = rng.normal(0.0, 0.08, size=(rows, 5)).astype(np.float64)
    y = (0.6 * x[:, 2] - 0.4 * x[:, 3] + 2.0 * x[:, 0] * x[:, 1]
         + 0.2 * np.sign(x[:, 4]) * np.abs(x[:, 0]) + rng.normal(0.0, 0.01, size=rows))
    return x, y.astype(np.float64)


def parity(cpu: np.ndarray, other: np.ndarray, target_scale: float) -> dict:
    """Prediction parity between two devices; pure function, tested without torch."""
    if cpu.shape != other.shape or cpu.ndim != 1 or len(cpu) < 2:
        raise ValueError("parity inputs must be equal-length vectors")
    if not np.isfinite(cpu).all() or not np.isfinite(other).all() or target_scale <= 0:
        raise ValueError("parity inputs must be finite with a positive target scale")
    diff = np.abs(cpu - other)
    correlation = float(np.corrcoef(cpu, other)[0, 1])
    max_diff = float(diff.max())
    return {
        "max_abs_diff": max_diff,
        "mean_abs_diff": float(diff.mean()),
        "correlation": correlation,
        "max_diff_fraction_of_target_sd": max_diff / target_scale,
        "within_declared_criteria": bool(
            correlation >= PARITY_MIN_CORRELATION and max_diff / target_scale <= PARITY_MAX_DIFF_FRACTION),
    }


def run_workload(device: str, rows: int, repeats: int, threads: int) -> dict:
    import torch

    from twenty.models import Neural, torch_device

    resolved = torch_device(device)
    torch.set_num_threads(threads)
    x, y = synthetic(rows)
    held_x, held_y = synthetic(4096, seed=SEED + 1)
    fit_seconds: list[float] = []
    predict_seconds: list[float] = []
    predictions: np.ndarray | None = None
    for _ in range(repeats):
        model = Neural(device=device, epochs=60, seed=SEED)
        started = time.perf_counter()
        model.fit(x, y)  # includes host-to-device transfer and device synchronisation
        fit_seconds.append(time.perf_counter() - started)
        started = time.perf_counter()
        predictions = model.predict(held_x)
        if resolved != "cpu":
            getattr(torch, resolved).synchronize()
        predict_seconds.append(time.perf_counter() - started)
    assert predictions is not None
    mse = float(np.mean((predictions - held_y) ** 2))
    return {
        "device_requested": device,
        "device_resolved": resolved,
        "rows": rows,
        "repeats": repeats,
        "fit_seconds_median": statistics.median(fit_seconds),
        "fit_seconds_all": fit_seconds,
        "predict_seconds_median": statistics.median(predict_seconds),
        "held_out_mse": mse,
        "peak_rss_mb": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024,
        "_predictions": predictions,
        "_target_sd": float(held_y.std()),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--device", choices=["cpu", "xpu"], required=True)
    parser.add_argument("--rows", default="6000,120000,900000")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--save-predictions", help="Write held-out predictions per row count to this .npz file")
    parser.add_argument("--compare", help="Compare held-out predictions against a saved .npz from another device")
    args = parser.parse_args()
    if args.repeats < 1 or args.repeats > 5 or args.threads < 1 or args.threads > 8:
        parser.error("repeats must be 1..5 and threads 1..8 (declared Quark bounds)")
    row_counts = [int(v) for v in args.rows.split(",") if v.strip()]
    if not row_counts or any(r < 2 or r > MAX_ROWS for r in row_counts):
        parser.error(f"rows must be within 2..{MAX_ROWS}")

    report: dict = {"host": {"platform": platform.platform(), "machine": platform.machine()},
                    "bounds": {"cpu_threads": args.threads, "repeats": args.repeats, "max_rows": MAX_ROWS},
                    "results": []}
    try:
        import torch

        report["torch"] = torch.__version__
        report["xpu_available"] = bool(hasattr(torch, "xpu") and torch.xpu.is_available())
        if report["xpu_available"]:
            report["xpu_device"] = torch.xpu.get_device_name(0)
        if args.device == "xpu" and not report["xpu_available"]:
            print(json.dumps(report, indent=2))
            return 3
        saved: dict[str, np.ndarray] = {}
        reference = np.load(args.compare) if args.compare else None
        for rows in row_counts:
            result = run_workload(args.device, rows, args.repeats, args.threads)
            predictions = result.pop("_predictions")
            target_sd = result.pop("_target_sd")
            if reference is not None:
                result["parity_vs_reference"] = parity(reference[f"rows_{rows}"], predictions, target_sd)
            saved[f"rows_{rows}"] = predictions
            report["results"].append(result)
        if args.save_predictions:
            np.savez(args.save_predictions, **saved)
    except ImportError:
        report["torch"] = None
        report["xpu_available"] = False
        print(json.dumps(report, indent=2))
        return 3 if args.device == "xpu" else 1
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
