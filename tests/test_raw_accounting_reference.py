"""Authored arithmetic parity, independent of the C# execution and evidence gates."""

import copy
import json
import unittest
from pathlib import Path

from twenty.raw_accounting_reference import replay

FIXTURE = Path(__file__).resolve().parents[1] / "fixtures/research/raw-accounting-parity.v1.json"


class RawAccountingReferenceTests(unittest.TestCase):
    def test_shared_scenarios_check_every_checkpoint(self):
        scenarios = json.loads(FIXTURE.read_text())["scenarios"]
        self.assertGreaterEqual(len(scenarios), 6)
        for scenario in scenarios:
            with self.subTest(scenario=scenario["id"]):
                original = copy.deepcopy(scenario)
                self.assertEqual([e["expected"] for e in scenario["events"]], replay(scenario))
                self.assertEqual(original, scenario)

    def test_expected_answers_do_not_drive_the_reference(self):
        scenario = json.loads(FIXTURE.read_text())["scenarios"][0]
        baseline = replay(scenario)
        for event in scenario["events"]:
            event.pop("expected")
        self.assertEqual(baseline, replay(scenario))

    def test_future_mark_does_not_change_earlier_cash_or_claims(self):
        scenario = json.loads(FIXTURE.read_text())["scenarios"][-1]
        baseline = replay(scenario)
        scenario["events"][-1]["fx"] = "0.01"
        self.assertEqual(baseline[:-1], replay(scenario)[:-1])
        self.assertNotEqual(baseline[-1]["equity"], replay(scenario)[-1]["equity"])

    def test_caller_decimal_context_cannot_round_the_reference(self):
        from decimal import localcontext
        scenario = json.loads(FIXTURE.read_text())["scenarios"][0]
        baseline = replay(scenario)
        with localcontext() as context:
            context.prec = 3
            self.assertEqual(baseline, replay(scenario))

    def test_invalid_operations_refuse_instead_of_silent_skips(self):
        base = {"currency": "GBP", "quoteUnit": "major", "events": []}
        bad = [
            [{"kind": "unknown"}],
            [{"kind": "deposit", "amount": "NaN"}],
            [{"kind": "deposit", "amount": 1.1}],
            [{"kind": "deposit", "amount": "-1"}],
            [{"kind": "fill", "quantity": "-1", "price": "10", "fx": "1", "fee": "0"}],
            [{"kind": "deposit", "amount": "1"},
             {"kind": "fill", "quantity": "1", "price": "10", "fx": "1", "fee": "0"}],
        ]
        for events in bad:
            with self.subTest(events=events), self.assertRaises((ValueError, TypeError)):
                replay(base | {"events": events})

    def test_claims_cannot_be_paid_twice_or_marked_as_settled_early(self):
        cash = json.loads(FIXTURE.read_text())["scenarios"][0]
        stock = json.loads(FIXTURE.read_text())["scenarios"][1]
        invalid = []
        duplicate = copy.deepcopy(cash)
        duplicate["events"].insert(-1, copy.deepcopy(duplicate["events"][-2]))
        invalid.append(duplicate)
        pending = copy.deepcopy(cash)
        pending["events"] = pending["events"][:4] + [pending["events"][-1]]
        invalid.append(pending)
        wrong_tax = copy.deepcopy(cash)
        wrong_tax["events"][4]["withheld"] = "221"
        invalid.append(wrong_tax)
        wrong_delivery = copy.deepcopy(stock)
        wrong_delivery["events"][4]["quantity"] = "4"
        invalid.append(wrong_delivery)
        for scenario in invalid:
            with self.subTest(scenario=scenario["id"], events=scenario["events"]), self.assertRaises(ValueError):
                replay(scenario)
