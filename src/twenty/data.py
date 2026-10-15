"""Monthly GBP total-return panels and deliberately lagged price features."""

import csv
import hashlib
import json
from dataclasses import dataclass
from datetime import date
from itertools import pairwise
from pathlib import Path

import numpy as np

FEATURE_NAMES = ("momentum_1m", "momentum_3m", "momentum_12m_skip_1m", "volatility_12m", "market_momentum_3m")
WARMUP = 13


@dataclass(frozen=True)
class Panel:
    dates: tuple[date, ...]
    symbols: tuple[str, ...]
    indices: np.ndarray
    benchmark: str
    sha256: str
    synthetic: bool

    @property
    def assets(self) -> tuple[str, ...]:
        return tuple(symbol for symbol in self.symbols if symbol != self.benchmark)

    def column(self, symbol: str) -> int:
        return self.symbols.index(symbol)

    def features(self, decision_index: int) -> np.ndarray:
        # Decision at t; only prices through t-1 may enter features.
        # This intentionally gives a full monthly execution lag.
        if not WARMUP <= decision_index < len(self.dates) - 1:
            raise ValueError("Insufficient past or forward observations")
        t = decision_index
        columns = [self.column(symbol) for symbol in self.assets]
        prices = self.indices[:, columns]
        momentum_1 = prices[t - 1] / prices[t - 2] - 1
        momentum_3 = prices[t - 1] / prices[t - 4] - 1
        momentum_12_skip = prices[t - 2] / prices[t - 13] - 1
        log_returns = np.diff(np.log(prices[t - 13:t]), axis=0)
        volatility = log_returns.std(axis=0, ddof=1) * np.sqrt(12)
        market = self.indices[:, self.column(self.benchmark)]
        market_momentum = np.full(len(columns), market[t - 1] / market[t - 4] - 1)
        return np.column_stack((momentum_1, momentum_3, momentum_12_skip, volatility, market_momentum))

    def returns(self, decision_index: int) -> dict[str, float]:
        values = self.indices[decision_index + 1] / self.indices[decision_index] - 1
        return dict(zip(self.symbols, values.tolist(), strict=True))

    def labels(self, decision_index: int) -> np.ndarray:
        returns = self.returns(decision_index)
        return np.array([returns[symbol] - returns[self.benchmark] for symbol in self.assets])


def load_panel(path: Path) -> Panel:
    payload = path.read_bytes()
    records: dict[date, dict[str, float]] = {}
    with path.open(newline="") as handle:
        reader = csv.DictReader(handle)
        if reader.fieldnames != ["date", "symbol", "total_return_index_gbp"]:
            raise ValueError("Expected CSV columns: date,symbol,total_return_index_gbp")
        for row in reader:
            day = date.fromisoformat(row["date"])
            symbol = row["symbol"].strip()
            value = float(row["total_return_index_gbp"])
            if not symbol or not np.isfinite(value) or value <= 0:
                raise ValueError("Symbols must be non-empty and index values positive and finite")
            bucket = records.setdefault(day, {})
            if symbol in bucket:
                raise ValueError(f"Duplicate observation: {day} {symbol}")
            bucket[symbol] = value
    metadata_path = path.with_suffix(".metadata.json")
    if not metadata_path.is_file():
        raise ValueError("A .metadata.json sidecar identifying source, benchmark and synthetic status is required")
    metadata = json.loads(metadata_path.read_text())
    if not metadata.get("source") or type(metadata.get("synthetic")) is not bool:
        raise ValueError("Metadata requires source and an explicit boolean synthetic flag")
    if metadata.get("currency") != "GBP" or metadata.get("frequency") != "monthly":
        raise ValueError("Only GBP monthly total-return panels are supported")
    dates = tuple(sorted(records))
    if len(dates) < WARMUP + 3:
        raise ValueError("At least 16 monthly observations are required")
    months = [day.year * 12 + day.month for day in dates]
    if any(b - a != 1 for a, b in pairwise(months)):
        raise ValueError("Exactly one observation date per consecutive calendar month is required")
    symbols = tuple(sorted(records[dates[0]]))
    if len(symbols) < 2 or metadata.get("benchmark") not in symbols:
        raise ValueError("Panel needs a benchmark and at least one other asset")
    for day, values in records.items():
        if set(values) != set(symbols):
            raise ValueError(f"Incomplete universe on {day}; missing returns must never be silently zero-filled")
    indices = np.array([[records[day][symbol] for symbol in symbols] for day in dates])
    digest = hashlib.sha256(payload + b"\0" + metadata_path.read_bytes()).hexdigest()
    return Panel(dates, symbols, indices, metadata["benchmark"], digest, metadata["synthetic"])


def make_synthetic(path: Path, months: int = 240, assets: int = 8, seed: int = 212) -> None:
    if months < 16 or assets < 1:
        raise ValueError("Need at least 16 months and one asset")
    if path.exists() or path.with_suffix(".metadata.json").exists():
        raise ValueError("Refusing to overwrite an existing dataset")
    rng = np.random.default_rng(seed)
    market = rng.normal(0.004, 0.04, size=months - 1)
    returns = np.column_stack((market, market[:, None] + rng.normal(0, 0.035, (months - 1, assets))))
    returns = np.clip(returns, -0.8, 0.8)
    indices = np.vstack((np.ones(assets + 1) * 100, 100 * np.cumprod(1 + returns, axis=0)))
    symbols = ["SYNTH_BENCH"] + [f"SYNTH_{i:02d}" for i in range(assets)]
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerow(["date", "symbol", "total_return_index_gbp"])
        for i, values in enumerate(indices):
            day = date(2000 + i // 12, i % 12 + 1, 1)
            for symbol, value in zip(symbols, values, strict=True):
                writer.writerow([day.isoformat(), symbol, f"{value:.12f}"])
    path.with_suffix(".metadata.json").write_text(json.dumps({
        "source": "Twenty seeded random synthetic fixture; NOT historical market data",
        "synthetic": True, "benchmark": "SYNTH_BENCH", "currency": "GBP",
        "frequency": "monthly", "seed": seed,
    }, indent=2) + "\n")
