import importlib.util
import sys
import unittest
from importlib.util import find_spec
from pathlib import Path

import numpy as np

SCRIPT = Path(__file__).resolve().parent.parent / "scripts" / "benchmark_accelerator.py"
SPEC = importlib.util.spec_from_file_location("benchmark_accelerator", SCRIPT)
bench = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = bench
SPEC.loader.exec_module(bench)


class BenchmarkHelperTests(unittest.TestCase):
    def test_synthetic_is_deterministic_bounded_and_finite(self):
        x, y = bench.synthetic(1000)
        again_x, again_y = bench.synthetic(1000)
        self.assertTrue(np.array_equal(x, again_x))
        self.assertTrue(np.array_equal(y, again_y))
        self.assertEqual((1000, 5), x.shape)
        self.assertTrue(np.isfinite(x).all() and np.isfinite(y).all())
        other_x, _ = bench.synthetic(1000, seed=999)
        self.assertFalse(np.array_equal(x, other_x))
        with self.assertRaises(ValueError):
            bench.synthetic(1)
        with self.assertRaises(ValueError):
            bench.synthetic(bench.MAX_ROWS + 1)

    def test_parity_reports_known_agreement_and_disagreement(self):
        base = np.linspace(-1.0, 1.0, 256)
        same = bench.parity(base, base.copy(), target_scale=0.5)
        self.assertEqual(0.0, same["max_abs_diff"])
        self.assertAlmostEqual(1.0, same["correlation"])
        self.assertTrue(same["within_declared_criteria"])
        shifted = bench.parity(base, base + 0.4, target_scale=0.5)
        self.assertAlmostEqual(0.4, shifted["max_abs_diff"])
        self.assertFalse(shifted["within_declared_criteria"])  # 0.4 is 80% of target sd, over the 5% bound
        with self.assertRaises(ValueError):
            bench.parity(base, base[:-1], target_scale=0.5)
        with self.assertRaises(ValueError):
            bench.parity(base, np.full_like(base, np.nan), target_scale=0.5)
        constant = np.full(16, 0.25)
        identical = bench.parity(constant, constant.copy(), target_scale=0.5)
        self.assertEqual(1.0, identical["correlation"])
        self.assertTrue(identical["within_declared_criteria"])
        self.assertEqual(0.0, bench.parity(constant, base[:16], target_scale=0.5)["correlation"])

    @unittest.skipUnless(find_spec("torch"), "Optional PyTorch dependency is not installed")
    def test_cpu_workload_smoke_is_finite_and_bounded(self):
        result = bench.run_workload("cpu", rows=512, repeats=1, threads=2)
        self.assertEqual("cpu", result["device_resolved"])
        self.assertEqual(512, result["rows"])
        self.assertTrue(np.isfinite(result["_predictions"]).all())
        self.assertGreater(result["fit_seconds_median"], 0)
        self.assertGreater(result["peak_rss_mb"], 0)


if __name__ == "__main__":
    unittest.main()
