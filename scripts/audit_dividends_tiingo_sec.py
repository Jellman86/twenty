"""Independent corporate-action sample: Tiingo cash dividends vs SEC declared dividends.

Caller supplies a captured Tiingo daily-history payload and an SEC Company Facts bulk
archive; nothing is fetched and no payload is committed. Tiingo `divCash` values drive
its adjusted prices, so they are checked against a genuinely independent producer:
the issuer's own SEC filings (`us-gaap:CommonStockDividendsPerShareDeclared`).

That SEC concept is a *duration* total declared per share over a fiscal period, with
year-to-date and annual accumulations republished across filings. Only single-quarter
points (60-110 day durations) are comparable to one dividend event. Later filings
republish comparatives split-adjusted (genuinely: the same quarter at 2.65 originally
and 0.38 after a 7:1 split), so each quarter takes its **earliest-filed** value — the
amount as declared at the time, which is what Tiingo's as-at-the-time `divCash`
records. Where the issuer stopped tagging a fiscal Q4 quarter directly (genuinely:
Apple from FY2018), the Q4 amount is *derived* as annual minus nine-month YTD from
earliest vintages and flagged as derived; non-positive derivations (mixed
split vintages) are skipped. Matching is one-to-one, amount-exact to the cent, Tiingo ex-date inside the
SEC fiscal period (the declaration and its ex-date fall in the same quarter for this
issuer) with a small declared spillover after period end. Unmatched rows on either
side are reported, never silently dropped. This audit certifies neither source; it
measures their agreement on a declared sample.
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
import zipfile
from datetime import date, timedelta
from decimal import Decimal, InvalidOperation
from pathlib import Path

MAX_BYTES = 64 * 1024 * 1024
SPILLOVER_DAYS = 14


class AuditError(Exception):
    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


def _decimal(value: object, code: str) -> Decimal:
    try:
        result = Decimal(str(value))
    except InvalidOperation:
        raise AuditError(code) from None
    if not result.is_finite():
        raise AuditError(code)
    return result


def parse_tiingo(payload: bytes) -> tuple[list[tuple[date, Decimal]], list[tuple[date, Decimal]]]:
    if not payload or len(payload) > MAX_BYTES:
        raise AuditError("tiingo-shape")
    try:
        rows = json.loads(payload)
    except (json.JSONDecodeError, UnicodeDecodeError):
        raise AuditError("tiingo-json") from None
    if not isinstance(rows, list) or not rows:
        raise AuditError("tiingo-shape")
    dividends: list[tuple[date, Decimal]] = []
    splits: list[tuple[date, Decimal]] = []
    seen: set[date] = set()
    for row in rows:
        if not isinstance(row, dict) or "date" not in row or "divCash" not in row or "splitFactor" not in row:
            raise AuditError("tiingo-row")
        try:
            day = date.fromisoformat(str(row["date"])[:10])
        except ValueError:
            raise AuditError("tiingo-date") from None
        if day in seen:
            raise AuditError("tiingo-duplicate-date")
        seen.add(day)
        div = _decimal(row["divCash"], "tiingo-div")
        if div < 0:
            raise AuditError("tiingo-div")
        if div > 0:
            dividends.append((day, div))
        factor = _decimal(row["splitFactor"], "tiingo-split")
        if factor != 1:
            splits.append((day, factor))
    dividends.sort()
    return dividends, splits


def _earliest_vintages(points: list, low_days: int, high_days: int) \
        -> tuple[dict[tuple[date, date], Decimal], int]:
    vintages: dict[tuple[date, date], list[tuple[date, Decimal]]] = {}
    for start, end, filed, value in points:
        if low_days <= (end - start).days <= high_days:
            vintages.setdefault((start, end), []).append((filed, value))
    earliest: dict[tuple[date, date], Decimal] = {}
    republished = 0
    for period, versions in vintages.items():
        versions.sort()
        first_values = {value for filed, value in versions if filed == versions[0][0]}
        if len(first_values) != 1:
            raise AuditError("sec-quarter-conflict")
        if len({value for _, value in versions}) > 1:
            republished += 1  # later vintages republished this period (e.g. split-adjusted)
        earliest[period] = first_values.pop()
    return earliest, republished


def parse_sec_quarters(entry: dict) -> tuple[list[tuple[date, date, Decimal, bool]], int]:
    try:
        raw = entry["facts"]["us-gaap"]["CommonStockDividendsPerShareDeclared"]["units"]["USD/shares"]
    except (KeyError, TypeError):
        raise AuditError("sec-concept-missing") from None
    points: list[tuple[date, date, date, Decimal]] = []
    for point in raw:
        if not isinstance(point, dict) or "start" not in point or "end" not in point \
                or "val" not in point or "filed" not in point:
            raise AuditError("sec-point")
        try:
            start = date.fromisoformat(point["start"])
            end = date.fromisoformat(point["end"])
            filed = date.fromisoformat(point["filed"])
        except ValueError:
            raise AuditError("sec-date") from None
        value = _decimal(point["val"], "sec-val")
        if value < 0:
            raise AuditError("sec-val")
        points.append((start, end, filed, value))
    quarters, republished = _earliest_vintages(points, 60, 110)
    ytd, _ = _earliest_vintages(points, 240, 300)
    annual, _ = _earliest_vintages(points, 350, 380)
    result: list[tuple[date, date, Decimal, bool]] = [
        (start, end, value, False) for (start, end), value in quarters.items() if value > 0]
    direct_ends = {end for _, end, _, _ in result}
    for (a_start, a_end), a_value in annual.items():
        if a_end in direct_ends:
            continue
        candidates = [(end, value) for (start, end), value in ytd.items()
                      if start == a_start and end < a_end]
        if len(candidates) != 1:
            continue
        y_end, y_value = candidates[0]
        derived = a_value - y_value
        if derived > 0:
            result.append((y_end.fromordinal(y_end.toordinal() + 1), a_end, derived, True))
    return sorted(result), republished


def compare(tiingo: list[tuple[date, Decimal]], sec: list[tuple[date, date, Decimal, bool]],
            spillover: int = SPILLOVER_DAYS) -> dict:
    candidates = []
    for ti, (t_day, t_val) in enumerate(tiingo):
        for si, (s_start, s_end, s_val, _) in enumerate(sec):
            if t_val == s_val and s_start <= t_day <= s_end + timedelta(days=spillover):
                distance = 0 if t_day <= s_end else (t_day - s_end).days
                candidates.append((distance, ti, si, (t_day - s_end).days))
    candidates.sort()
    used_t: set[int] = set()
    used_s: set[int] = set()
    gaps = []
    matched_derived = 0
    for _, ti, si, gap in candidates:
        if ti in used_t or si in used_s:
            continue
        used_t.add(ti)
        used_s.add(si)
        gaps.append(gap)
        matched_derived += 1 if sec[si][3] else 0
    return {
        "tiingoDividends": len(tiingo),
        "secQuarterDeclarations": len(sec),
        "matched": len(gaps),
        "matchedDirect": len(gaps) - matched_derived,
        "matchedDerivedQ4": matched_derived,
        "daysFromPeriodEnd": {
            "min": min(gaps) if gaps else None,
            "median": statistics.median(gaps) if gaps else None,
            "max": max(gaps) if gaps else None,
        },
        "unmatchedTiingo": [{"date": tiingo[i][0].isoformat(), "amount": str(tiingo[i][1])}
                            for i in range(len(tiingo)) if i not in used_t],
        "unmatchedSec": [{"periodStart": sec[i][0].isoformat(), "periodEnd": sec[i][1].isoformat(),
                          "amount": str(sec[i][2]), "derived": sec[i][3]}
                         for i in range(len(sec)) if i not in used_s],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Compare Tiingo divCash against SEC declared dividends per share.")
    parser.add_argument("--tiingo", required=True)
    parser.add_argument("--bulk", required=True)
    parser.add_argument("--cik", required=True)
    parser.add_argument("--start", required=True)
    parser.add_argument("--end", required=True)
    args = parser.parse_args(argv)
    try:
        if not (args.cik.isdigit() and 1 <= len(args.cik) <= 10):
            raise AuditError("audit-cik")
        dividends, splits = parse_tiingo(Path(args.tiingo).read_bytes())
        with zipfile.ZipFile(args.bulk) as archive:
            names = [n for n in archive.namelist() if n == f"CIK{int(args.cik):010d}.json"]
            if len(names) != 1:
                raise AuditError("sec-cik-missing")
            payload = archive.read(names[0])
            if len(payload) > MAX_BYTES:
                raise AuditError("sec-entry-size")
            entry = json.loads(payload)
        start, end = date.fromisoformat(args.start), date.fromisoformat(args.end)
        if start > end:
            raise AuditError("audit-window")
        in_window = [(day, val) for day, val in dividends if start <= day <= end]
        all_quarters, republished = parse_sec_quarters(entry)
        quarters = [q for q in all_quarters if q[1] >= start - timedelta(days=SPILLOVER_DAYS) and q[0] <= end]
        result = compare(in_window, quarters)
        result["secQuartersWithLaterRepublishedValues"] = republished
        result["window"] = {"start": start.isoformat(), "end": end.isoformat()}
        result["tiingoSplits"] = [{"date": day.isoformat(), "factor": str(factor)} for day, factor in splits
                                  if start <= day <= end]
    except AuditError as error:
        print(json.dumps({"valid": False, "code": error.code}))
        return 1
    except (OSError, zipfile.BadZipFile):
        print(json.dumps({"valid": False, "code": "audit-input-unreadable"}))
        return 1
    print(json.dumps({"valid": True, **result}, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
