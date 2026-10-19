import importlib.util
import json
import sys
import unittest
from datetime import date
from decimal import Decimal
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent.parent / "scripts" / "audit_dividends_tiingo_sec.py"
SPEC = importlib.util.spec_from_file_location("audit_dividends_tiingo_sec", SCRIPT)
audit = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = audit
SPEC.loader.exec_module(audit)


def tiingo_payload() -> bytes:
    return json.dumps([
        {"date": "2023-11-10T00:00:00.000Z", "divCash": 0.24, "splitFactor": 1},
        {"date": "2024-02-09T00:00:00.000Z", "divCash": 0.25, "splitFactor": 1},
        {"date": "2024-03-01T00:00:00.000Z", "divCash": 0, "splitFactor": 2},
        {"date": "2024-08-12T00:00:00.000Z", "divCash": 0.25, "splitFactor": 1},
    ]).encode()


def sec_entry() -> dict:
    points = [
        {"start": "2023-10-01", "end": "2023-12-30", "val": 0.24, "form": "10-Q", "filed": "2024-02-02"},
        {"start": "2023-10-01", "end": "2023-12-30", "val": 0.24, "form": "10-Q", "filed": "2024-05-03"},  # republished comparative
        {"start": "2023-10-01", "end": "2023-12-30", "val": 0.12, "form": "10-K", "filed": "2024-11-01"},  # split-adjusted vintage
        {"start": "2023-12-31", "end": "2024-03-30", "val": 0.25, "form": "10-Q", "filed": "2024-05-03"},
        {"start": "2023-10-01", "end": "2024-03-30", "val": 0.49, "form": "10-Q", "filed": "2024-05-03"},  # 6-month YTD
        {"start": "2023-10-01", "end": "2024-06-29", "val": 0.74, "form": "10-Q", "filed": "2024-08-02"},  # 9-month YTD
        {"start": "2023-10-01", "end": "2024-09-28", "val": 0.99, "form": "10-K", "filed": "2024-11-01"},  # annual; Q4 derived 0.25
    ]
    return {"facts": {"us-gaap": {"CommonStockDividendsPerShareDeclared": {"units": {"USD/shares": points}}}}}


class DividendIndependentAuditTests(unittest.TestCase):
    def test_quarter_filter_dedupes_and_drops_accumulations(self):
        quarters, republished = audit.parse_sec_quarters(sec_entry())
        self.assertEqual([(date(2023, 10, 1), date(2023, 12, 30), Decimal("0.24"), False),
                          (date(2023, 12, 31), date(2024, 3, 30), Decimal("0.25"), False),
                          (date(2024, 6, 30), date(2024, 9, 28), Decimal("0.25"), True)], quarters)
        self.assertEqual(1, republished)  # earliest-filed value wins over the split-adjusted vintage
        dividends, splits = audit.parse_tiingo(tiingo_payload())
        self.assertEqual(3, len(dividends))
        self.assertEqual([(date(2024, 3, 1), Decimal(2))], splits)

    def test_one_to_one_matching_reports_both_leftovers(self):
        dividends, _ = audit.parse_tiingo(tiingo_payload())
        result = audit.compare(dividends, audit.parse_sec_quarters(sec_entry())[0])
        self.assertEqual(3, result["matched"])
        self.assertEqual(2, result["matchedDirect"])
        self.assertEqual(1, result["matchedDerivedQ4"])
        self.assertEqual([], result["unmatchedTiingo"])
        self.assertEqual([], result["unmatchedSec"])
        # Direct ex-dates fall 50 days before their fiscal period ends; derived Q4 is -47.
        self.assertEqual(-50, result["daysFromPeriodEnd"]["median"])

    def test_amount_mismatch_never_matches(self):
        entry = sec_entry()
        points = entry["facts"]["us-gaap"]["CommonStockDividendsPerShareDeclared"]["units"]["USD/shares"]
        points[0]["val"] = 0.23
        points[1]["val"] = 0.23
        dividends, _ = audit.parse_tiingo(tiingo_payload())
        result = audit.compare(dividends, audit.parse_sec_quarters(entry)[0])
        self.assertEqual(2, result["matched"])  # Q2 direct + derived Q4; the altered Q1 matches nothing
        self.assertEqual([{"date": "2023-11-10", "amount": "0.24"}], result["unmatchedTiingo"])
        self.assertEqual(1, len(result["unmatchedSec"]))

    def test_malformed_inputs_fail_closed(self):
        with self.assertRaises(audit.AuditError):
            audit.parse_tiingo(b"[]")
        with self.assertRaises(audit.AuditError):
            audit.parse_tiingo(json.dumps([{"date": "2024-01-01", "divCash": -1, "splitFactor": 1}]).encode())
        with self.assertRaises(audit.AuditError):
            audit.parse_sec_quarters({"facts": {}})


if __name__ == "__main__":
    unittest.main()
