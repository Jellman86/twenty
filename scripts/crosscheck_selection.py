"""Independent stdlib-only cross-check for SelectionDiagnostics (CSCV PBO + deflated Sharpe).

Reads a candidate-return CSV (first column an index, then one column per candidate),
recomputes the same quantities as Twenty.Core's SelectionDiagnostics from the defining
formulas, and prints them as JSON for comparison against the C# engine run on the
identical file. It exists so the machine-precision agreement recorded in
docs/search-diagnostics.md stays reproducible. This mirrors the C# conventions exactly,
including the zero floor on the expected-maximum threshold and fractional ranks.

Usage: python3 scripts/crosscheck_selection.py <family.csv> <registered-trials>
"""

import csv
import itertools
import json
import math
import sys
from statistics import NormalDist, mean, median, stdev

ND = NormalDist()


def sharpe(values: list[float]) -> float:
    if len(values) < 2:
        return 0.0
    sd = stdev(values)
    m = mean(values)
    if sd <= 1e-15:
        return 0.0 if m == 0 else math.copysign(1e15, m)
    return m / sd


def main() -> int:
    path, registered = sys.argv[1], int(sys.argv[2])
    with open(path) as handle:
        rows = list(csv.reader(handle))
    header, data = rows[0][1:], [[float(x) for x in r[1:]] for r in rows[1:]]
    cols = {h: [r[i] for r in data] for i, h in enumerate(header)}
    months = len(data)
    order = sorted(header)

    blocks_count = 16
    sizes = [months // blocks_count + (1 if b < months % blocks_count else 0) for b in range(blocks_count)]
    starts, acc = [], 0
    for size in sizes:
        starts.append(acc)
        acc += size
    blocks = [(starts[b], starts[b] + sizes[b]) for b in range(blocks_count)]

    logits, win_in, win_out, negative = [], [], [], 0
    for combo in itertools.combinations(range(blocks_count), blocks_count // 2):
        in_idx = [t for b in combo for t in range(*blocks[b])]
        out_idx = [t for b in range(blocks_count) if b not in combo for t in range(*blocks[b])]
        is_sr = {h: sharpe([cols[h][t] for t in in_idx]) for h in order}
        oos_sr = {h: sharpe([cols[h][t] for t in out_idx]) for h in order}
        winner = order[0]
        for h in order[1:]:
            if is_sr[h] > is_sr[winner]:
                winner = h
        below = sum(1 for h in order if oos_sr[h] < oos_sr[winner])
        tied = sum(1 for h in order if oos_sr[h] == oos_sr[winner])
        rank = below + (tied + 1) / 2
        omega = rank / (len(order) + 1)
        logits.append(math.log(omega / (1 - omega)))
        win_in.append(is_sr[winner])
        win_out.append(oos_sr[winner])
        negative += 1 if oos_sr[winner] < 0 else 0

    full_sr = {h: sharpe(cols[h]) for h in order}
    winner = order[0]
    for h in order[1:]:
        if full_sr[h] > full_sr[winner]:
            winner = h
    values = cols[winner]
    centre = mean(values)
    m2 = mean([(v - centre) ** 2 for v in values])
    m3 = mean([(v - centre) ** 3 for v in values])
    m4 = mean([(v - centre) ** 4 for v in values])
    skew, kurt = m3 / m2 ** 1.5, m4 / m2 ** 2
    sr = full_sr[winner]
    trial_sd = stdev([full_sr[h] for h in order])

    def psr(threshold: float) -> float:
        return ND.cdf((sr - threshold) * math.sqrt(months - 1)
                      / math.sqrt(1 - skew * sr + ((kurt - 1) / 4) * sr ** 2))

    def expected_maximum(trials: float) -> float:
        if trials <= 1:
            return 0.0
        gamma = 0.5772156649
        raw = trial_sd * ((1 - gamma) * ND.inv_cdf(1 - 1 / trials)
                          + gamma * ND.inv_cdf(1 - 1 / (trials * math.e)))
        return max(0.0, raw)

    correlations = []
    for a, b in itertools.combinations(order, 2):
        ma, mb = mean(cols[a]), mean(cols[b])
        cov = sum((x - ma) * (y - mb) for x, y in zip(cols[a], cols[b], strict=True))
        va = sum((x - ma) ** 2 for x in cols[a])
        vb = sum((y - mb) ** 2 for y in cols[b])
        if va > 1e-30 and vb > 1e-30:
            correlations.append(cov / math.sqrt(va * vb))
    rho = min(1.0, max(0.0, mean(correlations))) if correlations else None
    count = len(order)
    sensitivities = []
    for basis, trials in [("family-as-independent", float(count)),
                          ("family-correlation-adjusted", None if rho is None else rho + (1 - rho) * count),
                          ("registered-snapshot-as-independent", float(registered))]:
        if trials is None or basis.startswith("registered") and registered <= count:
            continue
        threshold = expected_maximum(trials)
        sensitivities.append({"basis": basis, "trials": trials, "emax": threshold, "dsr": psr(threshold)})

    print(json.dumps({
        "pbo": {"combinations": len(logits),
                "pbo": sum(1 for x in logits if x < 0) / len(logits),
                "medianLogit": median(logits), "medianIn": median(win_in),
                "medianOut": median(win_out), "fracNeg": negative / len(logits)},
        "winner": winner, "sharpe": sr, "skew": skew, "kurt": kurt, "trialSd": trial_sd,
        "avgCorr": rho, "psr0": psr(0.0), "sens": sensitivities}, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
