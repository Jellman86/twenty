import copy
import importlib.util
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "audit_listing_prices", Path(__file__).resolve().parent.parent / "scripts/audit_listing_prices.py")
audit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit)


def fixture():
    captured = "2026-01-03T12:00:00+00:00"
    evidence = {"publishedAt": captured, "availableAt": captured,
                "availabilityEvidence": "observedPublication"}
    listing = {"kind": "listing", "listingId": "aaa", "status": "active",
               "providerIds": [{"namespace": "tiingo.ticker", "value": "AAA"}],
               "calendarId": "ny", "effectiveFrom": "2020-01-01T00:00:00Z", "effectiveUntil": None}
    row = {"open": 10, "high": 12, "low": 9, "close": 11, "volume": 100,
           "adjOpen": 5, "adjHigh": 6, "adjLow": 4.5, "adjClose": 5.5,
           "adjVolume": 200, "divCash": 1, "splitFactor": 1}
    bars = []
    for basis in ("raw", "totalReturnAdjusted"):
        bar = {"kind": "priceBar", "listingId": "aaa", "basis": basis,
               "sessionDate": "2026-01-02", "sessionCloseAt": "2026-01-02T21:00:00Z",
               "denomination": {"currency": "USD", "unit": "major"}, "evidence": evidence}
        for field in ("open", "high", "low", "close", "volume"):
            bar[field] = row["adj" + field.capitalize() if basis != "raw" else field]
        bars.append(bar)
    dividend = {"kind": "corporateAction", "listingId": "aaa", "action": "dividend",
                "effectiveAt": "2026-01-02T21:00:00Z", "cashPerShare": 1,
                "cashCurrency": "USD", "evidence": evidence}
    dataset = {"manifest": {"calendars": [{"id": "ny", "timeZone": "America/New_York"}]},
               "observations": [listing, *bars, dividend]}
    return dataset, {("aaa", audit.instant(captured)): {"2026-01-02": row}}


class ListingPriceWireAuditTests(unittest.TestCase):
    def test_both_bases_and_action_match_the_wire(self):
        dataset, captures = fixture()
        result = audit.audit(dataset, captures)
        self.assertEqual(2, result["checkedBars"])
        self.assertEqual({"dividend": 1, "raw": 1, "totalReturnAdjusted": 1}, result["counts"])

    def test_price_volume_time_and_action_tampering_are_detected(self):
        for field, value in [("close", 99), ("volume", 1),
                             ("sessionCloseAt", "2026-01-02T20:00:00Z")]:
            with self.subTest(field=field):
                dataset, captures = fixture()
                dataset["observations"][1][field] = value
                with self.assertRaises(ValueError):
                    audit.audit(dataset, captures)
        dataset, captures = fixture()
        dataset["observations"][-1]["cashPerShare"] = 2
        with self.assertRaises(ValueError):
            audit.audit(dataset, captures)

    def test_omitted_bars_and_actions_are_detected(self):
        for index in (1, 3):
            with self.subTest(index=index):
                dataset, captures = fixture()
                del dataset["observations"][index]
                with self.assertRaises(ValueError):
                    audit.audit(dataset, captures)

    def test_delisted_identity_interval_does_not_hide_earlier_prices(self):
        dataset, captures = fixture()
        closed = copy.deepcopy(dataset["observations"][0])
        dataset["observations"][0]["effectiveUntil"] = "2026-01-03T00:00:00Z"
        closed.update(status="delisted", effectiveFrom="2026-01-03T00:00:00Z")
        dataset["observations"].append(closed)
        self.assertTrue(audit.audit(dataset, captures)["valid"])
