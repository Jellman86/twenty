"""Shared fixture protects the cross-language contribution convention."""
import json
import unittest
from pathlib import Path

from twenty.finance import future_value, required_return


class ProjectionContractTests(unittest.TestCase):
    def test_shared_golden_values(self):
        rows = json.loads((Path(__file__).parents[1] / "fixtures/projections.json").read_text())
        for row in rows:
            with self.subTest(row=row):
                actual = future_value(row["monthly"], row["years"], row["annualReturn"])
                self.assertAlmostEqual(actual / row["balance"], 1, places=11)
                if row["requiredReturn"] is None:
                    with self.assertRaises(ValueError):
                        required_return(row["monthly"], row["years"], 1e6)
                else:
                    self.assertAlmostEqual(required_return(row["monthly"], row["years"], 1e6), row["requiredReturn"], places=9)
