"""Expanding-window evaluation with a strict label-availability gap."""

import csv
import hashlib
import json
import math
import platform
import time
import uuid
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path

import numpy as np

from . import __version__
from .data import FEATURE_NAMES, WARMUP, Panel
from .models import Neural, Ridge, torch_device
from .simulation import Costs, Portfolio


@dataclass(frozen=True)
class Experiment:
    monthly_gbp: float = 20.0
    model: str = "ridge"
    device: str = "cpu"
    train_months: int = 60
    refit_months: int = 12
    holdout_months: int = 24
    evaluate_holdout: bool = False
    top_k: int = 4
    epochs: int = 60
    seed: int = 212

    def __post_init__(self) -> None:
        if not math.isfinite(self.monthly_gbp) or self.monthly_gbp <= 0:
            raise ValueError("Monthly contribution must be positive and finite")
        if self.model not in ("ridge", "mlp"):
            raise ValueError("Model must be ridge or mlp")
        if self.device not in ("cpu", "auto", "mps", "cuda", "xpu", "hpu"):
            raise ValueError("Invalid training device")
        if self.model == "ridge" and self.device != "cpu":
            raise ValueError("Ridge uses NumPy on CPU; select mlp for accelerator training")
        if min(self.train_months, self.refit_months, self.holdout_months, self.top_k, self.epochs) < 1:
            raise ValueError("Training, refit, holdout, top-k and epochs must be positive")


def training_indices(decision_index: int) -> range:
    # Label for sample s ends at s+1; require s+1 < decision_index.
    return range(WARMUP, decision_index - 1)


def rank_weights(scores: np.ndarray, assets: tuple[str, ...], top_k: int) -> dict[str, float]:
    if scores.shape != (len(assets),) or not np.isfinite(scores).all():
        raise ValueError("Expected one finite score per tradable asset")
    count = min(top_k, len(assets))
    ordered = sorted(range(len(assets)), key=lambda i: (-float(scores[i]), assets[i]))[:count]
    return {assets[i]: 1 / count for i in ordered}


def run_experiment(panel: Panel, config: Experiment, costs: Costs, output: Path) -> Path:
    first = WARMUP + config.train_months + 1
    reserve_start = len(panel.dates) - 1 - config.holdout_months
    if reserve_start <= first:
        raise ValueError("Not enough data for training, development evaluation and reserved holdout")
    start = reserve_start if config.evaluate_holdout else first
    stop = len(panel.dates) - 1 if config.evaluate_holdout else reserve_start
    if config.model == "mlp":
        torch_device(config.device)  # Fail before producing incomplete run artifacts.
    output.mkdir(parents=True, exist_ok=True)
    run_id = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    destination = output / run_id
    destination.mkdir()
    provenance = {
        "run_id": run_id, "version": __version__, "dataset_sha256": panel.sha256,
        "code_sha256": hashlib.sha256(b"".join(path.name.encode() + b"\0" + path.read_bytes() for path in sorted(Path(__file__).parent.glob("*.py")))).hexdigest(),
        "synthetic": panel.synthetic, "config": asdict(config), "costs": asdict(costs),
        "features": list(FEATURE_NAMES), "label": "next-month asset return minus benchmark return",
        "evaluation_scope": "reserved_holdout" if config.evaluate_holdout else "development_walk_forward",
        "created_at": datetime.now(UTC).isoformat(),
        "python": platform.python_version(), "numpy": np.__version__,
    }
    # Record every attempted run before fitting, including failed trials.
    (destination / "manifest.json").write_text(json.dumps(provenance, indent=2) + "\n")
    with (output / "experiments.jsonl").open("a") as handle:
        handle.write(json.dumps(provenance) + "\n")
    portfolios = {name: Portfolio() for name in ("trained_model", "momentum", "equal_weight", "benchmark")}
    rows, folds, predictions = [], [], []
    all_started = time.perf_counter()
    for t in range(start, stop):
        if (t - start) % config.refit_months == 0:
            training = list(training_indices(t))
            x = np.vstack([panel.features(i) for i in training])
            y = np.concatenate([panel.labels(i) for i in training])
            model = Ridge() if config.model == "ridge" else Neural(config.device, config.epochs, config.seed)
            started = time.perf_counter()
            model.fit(x, y)
            fold = {"decision_date": panel.dates[t].isoformat(),
                    "train_first_decision": panel.dates[training[0]].isoformat(),
                    "train_last_label_end": panel.dates[training[-1] + 1].isoformat(),
                    "train_months": len(training), "train_rows": len(x),
                    "fit_seconds": time.perf_counter() - started}
            folds.append(fold)
            (destination / f"model-{panel.dates[t].isoformat()}.json").write_text(json.dumps({**fold, **model.export()}, indent=2) + "\n")
        features = panel.features(t)
        scores = model.predict(features)
        period_returns = panel.returns(t)
        allocation = {
            "trained_model": rank_weights(scores, panel.assets, config.top_k),
            "momentum": rank_weights(features[:, 2], panel.assets, config.top_k),
            "equal_weight": {symbol: 1 / len(panel.assets) for symbol in panel.assets},
            "benchmark": {panel.benchmark: 1.0},
        }
        for symbol, score in zip(panel.assets, scores, strict=True):
            predictions.append({"decision_date": panel.dates[t].isoformat(), "symbol": symbol,
                                "feature_cutoff": panel.dates[t - 1].isoformat(),
                                "score": float(score), "target_weight": allocation["trained_model"].get(symbol, 0.0)})
        for name, portfolio in portfolios.items():
            state = portfolio.step(config.monthly_gbp, allocation[name], period_returns, costs)
            rows.append({"decision_date": panel.dates[t].isoformat(), "period_end": panel.dates[t + 1].isoformat(),
                         "strategy": name, **state})
    summaries = {}
    for name, portfolio in portfolios.items():
        periods = [row["period_return"] for row in rows if row["strategy"] == name]
        volatility = float(np.std(periods, ddof=1) * np.sqrt(12)) if len(periods) > 1 else 0.0
        summaries[name] = {"ending_equity_gbp": portfolio.equity,
                           "contributions_gbp": portfolio.contributions,
                           "profit_gbp": portfolio.equity - portfolio.contributions,
                           "trading_cost_gbp": portfolio.costs_paid,
                           "annualised_time_weighted_return": portfolio.unit_price ** (12 / len(periods)) - 1,
                           "annualised_monthly_volatility": volatility,
                           "max_drawdown": portfolio.max_drawdown}
    warnings = [
        "Research simulation only; no live or demo broker connection exists.",
        "Fractional GBP notionals and a flat cost schedule approximate execution; not actual broker fills.",
        "Fixed-universe input may contain survivorship bias. Real results require a point-in-time universe and delisting treatment.",
        "Price-only prototype: fundamentals, filing signals, model selection and probability-of-overfitting calculations are not implemented.",
        "Software/data/electricity costs, per-instrument taxes, fund fees and foreign withholding are not added separately.",
        "No statistical significance or investment suitability is established by this report.",
    ]
    if panel.synthetic:
        warnings.insert(0, "SYNTHETIC DATA: generated random fixture. These results provide NO evidence of investment profitability.")
    report = {**provenance, "evaluation_start": panel.dates[start].isoformat(),
              "evaluation_end": panel.dates[stop].isoformat(), "evaluation_months": stop - start,
              "reserved_period_start": panel.dates[reserve_start].isoformat(),
              "resolved_device": model.export()["device"],
              "elapsed_seconds": time.perf_counter() - all_started,
              "summaries": summaries, "folds": folds, "limitations": warnings}
    for filename, records in (("equity.csv", rows), ("predictions.csv", predictions)):
        with (destination / filename).open("w", newline="") as handle:
            writer = csv.DictWriter(handle, fieldnames=list(records[0]))
            writer.writeheader()
            writer.writerows(records)
    (destination / "report.json").write_text(json.dumps(report, indent=2, allow_nan=False) + "\n")
    return destination
