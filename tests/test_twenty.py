import json
import tempfile
import unittest
from importlib.util import find_spec
from pathlib import Path

import numpy as np

from twenty.data import load_panel, make_synthetic
from twenty.finance import future_value, required_return
from twenty.models import Neural, Ridge, torch_device
from twenty.research import Experiment, run_experiment, training_indices
from twenty.simulation import Costs, Portfolio


class FinanceTests(unittest.TestCase):
    def test_zero_return_and_published_scenarios(self):
        self.assertEqual(future_value(20, 30, 0), 7200)
        self.assertAlmostEqual(future_value(20, 40, 0.07), 49430.84014194, places=6)
        self.assertAlmostEqual(required_return(20, 30, 1_000_000), 0.257412418949, places=10)

    def test_invalid_money_is_rejected(self):
        for contribution in (-1, float("nan"), float("inf")):
            with self.assertRaises(ValueError):
                future_value(contribution, 30, 0.07)


class AccountingTests(unittest.TestCase):
    def test_deposits_are_not_returns(self):
        portfolio = Portfolio()
        for _ in range(12):
            row = portfolio.step(20, {"A": 1}, {"A": 0}, Costs(0, 0, 0, 0))
        self.assertAlmostEqual(row["equity_gbp"], 240)
        self.assertAlmostEqual(row["unit_price"], 1)
        self.assertEqual(row["max_drawdown"], 0)

    def test_buy_reserves_costs_and_cannot_borrow(self):
        portfolio = Portfolio()
        row = portfolio.step(20, {"A": 1}, {"A": 0}, Costs(100, 0, 0, 0))
        self.assertAlmostEqual(row["equity_gbp"], 20 / 1.01)
        self.assertAlmostEqual(portfolio.costs_paid, 20 - 20 / 1.01)
        self.assertGreaterEqual(portfolio.cash, 0)

    def test_replacing_holding_charges_both_sides(self):
        portfolio = Portfolio()
        costs = Costs(100, 0, 0, 0)
        portfolio.step(100, {"A": 1}, {"A": 0, "B": 0}, costs)
        row = portfolio.step(0, {"B": 1}, {"A": 0, "B": 0}, costs)
        self.assertAlmostEqual(row["equity_gbp"], (100 / 1.01) * 0.99 / 1.01)

    def test_subminimum_allocation_stays_in_cash(self):
        portfolio = Portfolio()
        row = portfolio.step(0.50, {"A": 1}, {"A": 1}, Costs())
        self.assertEqual(row["equity_gbp"], 0.50)
        self.assertEqual(row["cash_gbp"], 0.50)
        self.assertEqual(portfolio.costs_paid, 0)

    def test_drawdown_ignores_new_deposit_recovery(self):
        portfolio = Portfolio()
        costs = Costs(0, 0, 0, 0)
        portfolio.step(100, {"A": 1}, {"A": -0.5}, costs)
        row = portfolio.step(100, {"A": 1}, {"A": 0}, costs)
        self.assertEqual(row["equity_gbp"], 150)
        self.assertEqual(row["max_drawdown"], -0.5)
        self.assertEqual(row["unit_price"], 0.5)

    def test_missing_return_or_leverage_fails_before_mutation(self):
        portfolio = Portfolio()
        for weights, returns in [({"A": 1.1}, {"A": 0}), ({"A": 1}, {})]:
            with self.assertRaises(ValueError):
                portfolio.step(20, weights, returns, Costs())
            self.assertEqual(portfolio.contributions, 0)
        with self.assertRaises(ValueError):
            Costs(fx_bps=float("nan"))


class ResearchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.path = self.root / "fixture.csv"
        make_synthetic(self.path, months=108, assets=4)
        self.panel = load_panel(self.path)

    def test_features_cannot_see_execution_or_future_prices(self):
        t = 40
        expected = self.panel.features(t).copy()
        self.panel.indices[t:] *= 3
        np.testing.assert_array_equal(expected, self.panel.features(t))

    def test_training_labels_end_strictly_before_decision(self):
        t = 40
        indices = list(training_indices(t))
        self.assertTrue(all(i + 1 < t for i in indices))
        self.assertEqual(indices[-1] + 1, t - 1)

    def test_ridge_scaling_uses_training_only(self):
        x = np.array([[1., 3.], [2., 5.], [3., 7.]])
        model = Ridge()
        model.fit(x, np.array([0.1, 0.2, 0.3]))
        mean = model.mean.copy()
        model.predict(np.array([[1e9, -1e9]]))
        np.testing.assert_array_equal(model.mean, mean)
        np.testing.assert_array_equal(mean, x.mean(axis=0))

    def test_future_holdout_changes_do_not_change_development_predictions(self):
        config = Experiment(train_months=24, holdout_months=12, top_k=2)
        first_run = run_experiment(self.panel, config, Costs(), self.root / "runs")
        reserve_start = len(self.panel.dates) - 1 - config.holdout_months
        self.panel.indices[reserve_start + 1:] *= 2
        second_run = run_experiment(self.panel, config, Costs(), self.root / "runs")
        self.assertEqual((first_run / "predictions.csv").read_text(), (second_run / "predictions.csv").read_text())
        self.assertEqual((first_run / "equity.csv").read_text(), (second_run / "equity.csv").read_text())

    def test_report_has_fair_cash_flows_and_preserves_holdout(self):
        config = Experiment(train_months=24, holdout_months=12, top_k=2)
        run = run_experiment(self.panel, config, Costs(), self.root / "runs")
        report = json.loads((run / "report.json").read_text())
        self.assertTrue(report["synthetic"])
        self.assertEqual(report["evaluation_scope"], "development_walk_forward")
        self.assertEqual(report["evaluation_end"], report["reserved_period_start"])
        self.assertTrue(all(fold["train_last_label_end"] < fold["decision_date"] for fold in report["folds"]))
        contributions = [s["contributions_gbp"] for s in report["summaries"].values()]
        self.assertEqual(set(report["summaries"]), {"trained_model", "momentum", "equal_weight", "benchmark"})
        self.assertEqual(len(set(contributions)), 1)
        self.assertEqual(contributions[0], report["evaluation_months"] * 20)
        self.assertTrue((run / "manifest.json").is_file())
        self.assertEqual(len((run.parent / "experiments.jsonl").read_text().splitlines()), 1)

    def test_equal_weight_is_independent_of_model_top_k(self):
        base = {"train_months": 24, "holdout_months": 12}
        first = run_experiment(self.panel, Experiment(**base, top_k=1), Costs(), self.root / "runs")
        second = run_experiment(self.panel, Experiment(**base, top_k=4), Costs(), self.root / "runs")
        first_report = json.loads((first / "report.json").read_text())
        second_report = json.loads((second / "report.json").read_text())
        self.assertEqual(first_report["summaries"]["equal_weight"], second_report["summaries"]["equal_weight"])

    def test_holdout_requires_explicit_flag_and_is_labelled(self):
        run = run_experiment(self.panel, Experiment(train_months=24, holdout_months=12, evaluate_holdout=True), Costs(), self.root / "runs")
        report = json.loads((run / "report.json").read_text())
        self.assertEqual(report["evaluation_scope"], "reserved_holdout")
        self.assertEqual(report["evaluation_months"], 12)
        self.assertEqual(report["evaluation_start"], report["reserved_period_start"])

    def test_missing_observation_is_not_filled(self):
        lines = self.path.read_text().splitlines()
        self.path.write_text("\n".join(lines[:9] + lines[10:]) + "\n")
        with self.assertRaisesRegex(ValueError, "Incomplete universe"):
            load_panel(self.path)

    def test_metadata_and_unique_dates_required(self):
        metadata = self.path.with_suffix(".metadata.json")
        metadata.unlink()
        with self.assertRaisesRegex(ValueError, "sidecar"):
            load_panel(self.path)

    def test_ridge_rejects_requested_accelerator(self):
        with self.assertRaisesRegex(ValueError, "Ridge uses"):
            Experiment(model="ridge", device="xpu")

    def test_demo_will_not_overwrite_data(self):
        with self.assertRaisesRegex(ValueError, "overwrite"):
            make_synthetic(self.path)


@unittest.skipUnless(find_spec("torch"), "Optional PyTorch dependency is not installed")
class NeuralTests(unittest.TestCase):
    def test_cpu_training_exports_finite_predictions_and_parameters(self):
        rng = np.random.default_rng(212)
        x = rng.normal(size=(128, 5))
        y = x[:, 0] * 0.01 - x[:, 1] * 0.02
        model = Neural("cpu", epochs=5)
        model.fit(x, y)
        result = model.predict(x[:4])
        self.assertEqual(result.shape, (4,))
        self.assertTrue(np.isfinite(result).all())
        self.assertEqual(model.export()["device"], "cpu")
        self.assertEqual(model.export()["precision"], "float32")
        json.dumps(model.export(), allow_nan=False)

    def test_unavailable_xpu_does_not_silently_fallback(self):
        import torch
        if hasattr(torch, "xpu") and torch.xpu.is_available():
            self.skipTest("Host has an available Intel XPU")
        with self.assertRaisesRegex(ValueError, "Intel XPU requested but unavailable"):
            torch_device("xpu")


if __name__ == "__main__":
    unittest.main()
