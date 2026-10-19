"""Independent-producer FX sample audit: ECB euro reference rates vs Bank of England daily spot rates.

Caller supplies both payloads (downloaded separately through each publisher's normal
flow); nothing is fetched here and no payload is committed. The ECB publishes currency
units per EUR; the Bank of England IADB series XUDLUSS/XUDLERS publish US dollars /
euros per GBP ("into sterling"). The comparable derived ECB quantities are therefore
EURUSD/EURGBP (USD per GBP) and 1/EURGBP (EUR per GBP); the inversion is audit
arithmetic on published values, disclosed as derived, never a domain FX conversion.

The producers fix at different times of day by different procedures, so equality is
NOT expected. This audit checks scale/direction/unit agreement and reports the
distribution of relative differences; it certifies neither fixing as an execution price.
"""

from __future__ import annotations

import argparse
import csv
import io
import json
import statistics
import sys
import xml.etree.ElementTree as ET
from datetime import date, datetime
from decimal import Decimal, InvalidOperation
from pathlib import Path

ECB_CUBE_NS = "{http://www.ecb.int/vocabulary/2002-08-01/eurofxref}Cube"
MAX_BYTES = 32 * 1024 * 1024


class AuditError(Exception):
    """Fail-closed audit rejection with a safe, stable code."""

    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


def _decimal(text: str, code: str) -> Decimal:
    try:
        value = Decimal(text)
    except InvalidOperation:
        raise AuditError(code) from None
    if not value.is_finite() or value <= 0:
        raise AuditError(code)
    return value


def parse_ecb_xml(payload: bytes) -> dict[date, dict[str, Decimal]]:
    if not payload or len(payload) > MAX_BYTES or b"<!DOCTYPE" in payload or b"<!ENTITY" in payload:
        raise AuditError("ecb-xml-shape")
    try:
        root = ET.fromstring(payload)
    except ET.ParseError:
        raise AuditError("ecb-xml-parse") from None
    days: dict[date, dict[str, Decimal]] = {}
    for cube in root.iter(ECB_CUBE_NS):
        stamp = cube.get("time")
        if stamp is None:
            continue
        try:
            day = date.fromisoformat(stamp)
        except ValueError:
            raise AuditError("ecb-xml-date") from None
        if day in days:
            raise AuditError("ecb-xml-duplicate-date")
        rates: dict[str, Decimal] = {}
        for entry in cube:
            if entry.tag != ECB_CUBE_NS:
                raise AuditError("ecb-xml-shape")
            currency, rate = entry.get("currency"), entry.get("rate")
            if currency is None or rate is None or currency in rates:
                raise AuditError("ecb-xml-entry")
            rates[currency] = _decimal(rate, "ecb-xml-rate")
        if not rates:
            raise AuditError("ecb-xml-empty-day")
        days[day] = rates
    if not days:
        raise AuditError("ecb-xml-empty")
    return days


def parse_boe_csv(payload: bytes) -> dict[date, dict[str, Decimal]]:
    if not payload or len(payload) > MAX_BYTES:
        raise AuditError("boe-csv-shape")
    try:
        text = payload.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise AuditError("boe-csv-encoding") from None
    reader = csv.reader(io.StringIO(text))
    rows = [row for row in reader if row and any(cell.strip() for cell in row)]
    if not rows or [cell.strip() for cell in rows[0]] != ["DATE", "XUDLUSS", "XUDLERS"]:
        raise AuditError("boe-csv-header")
    days: dict[date, dict[str, Decimal]] = {}
    for row in rows[1:]:
        if len(row) != 3:
            raise AuditError("boe-csv-row")
        try:
            day = datetime.strptime(row[0].strip(), "%d %b %Y").date()
        except ValueError:
            raise AuditError("boe-csv-date") from None
        if day in days:
            raise AuditError("boe-csv-duplicate-date")
        days[day] = {
            "XUDLUSS": _decimal(row[1].strip(), "boe-csv-rate"),
            "XUDLERS": _decimal(row[2].strip(), "boe-csv-rate"),
        }
    if not days:
        raise AuditError("boe-csv-empty")
    return days


def _bps_stats(diffs_bps: list[float]) -> dict[str, float | int]:
    absolute = sorted(abs(value) for value in diffs_bps)
    p95 = absolute[min(len(absolute) - 1, int(0.95 * len(absolute)))]
    return {
        "matchedDates": len(diffs_bps),
        "medianBps": round(statistics.median(diffs_bps), 3),
        "meanBps": round(statistics.fmean(diffs_bps), 3),
        "p95AbsBps": round(p95, 3),
        "maxAbsBps": round(absolute[-1], 3),
        "over10Bps": sum(1 for value in absolute if value > 10),
        "over50Bps": sum(1 for value in absolute if value > 50),
    }


def compare(ecb: dict[date, dict[str, Decimal]], boe: dict[date, dict[str, Decimal]],
            start: date, end: date) -> dict:
    if start > end:
        raise AuditError("audit-window")
    ecb_days = {day for day in ecb if start <= day <= end}
    boe_days = {day for day in boe if start <= day <= end}
    pairs: dict[str, list[float]] = {"usdPerGbp": [], "eurPerGbp": []}
    worst: dict[str, list[dict]] = {"usdPerGbp": [], "eurPerGbp": []}
    for day in sorted(ecb_days & boe_days):
        rates = ecb[day]
        if "GBP" not in rates or "USD" not in rates:
            raise AuditError("ecb-missing-leg")
        derived = {
            "usdPerGbp": (rates["USD"] / rates["GBP"], boe[day]["XUDLUSS"]),
            "eurPerGbp": (Decimal(1) / rates["GBP"], boe[day]["XUDLERS"]),
        }
        for name, (ecb_value, boe_value) in derived.items():
            bps = float((boe_value / ecb_value - 1) * 10000)
            pairs[name].append(bps)
            if abs(bps) > 50:
                worst[name].append({"date": day.isoformat(), "bps": round(bps, 2)})
    if not pairs["usdPerGbp"]:
        raise AuditError("audit-no-overlap")
    return {
        "window": {"start": start.isoformat(), "end": end.isoformat()},
        "ecbDatesInWindow": len(ecb_days),
        "boeDatesInWindow": len(boe_days),
        "ecbOnlyDates": [day.isoformat() for day in sorted(ecb_days - boe_days)],
        "boeOnlyDates": [day.isoformat() for day in sorted(boe_days - ecb_days)],
        "pairs": {name: _bps_stats(values) for name, values in pairs.items()},
        "over50BpsDates": worst,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Compare ECB reference FX against Bank of England daily spot rates.")
    parser.add_argument("--ecb-xml", required=True)
    parser.add_argument("--boe-csv", required=True)
    parser.add_argument("--start", required=True)
    parser.add_argument("--end", required=True)
    args = parser.parse_args(argv)
    try:
        result = compare(
            parse_ecb_xml(Path(args.ecb_xml).read_bytes()),
            parse_boe_csv(Path(args.boe_csv).read_bytes()),
            date.fromisoformat(args.start), date.fromisoformat(args.end))
    except AuditError as error:
        print(json.dumps({"valid": False, "code": error.code}))
        return 1
    except OSError:
        print(json.dumps({"valid": False, "code": "audit-input-unreadable"}))
        return 1
    print(json.dumps({"valid": True, **result}, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
