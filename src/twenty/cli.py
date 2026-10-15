import argparse
import json
import platform
import sys
from pathlib import Path

from .data import load_panel, make_synthetic
from .finance import future_value, required_return
from .research import Experiment, run_experiment
from .simulation import Costs


def parser() -> argparse.ArgumentParser:
    root = argparse.ArgumentParser(description="Twenty — local investment research and model training")
    commands = root.add_subparsers(dest="command", required=True)
    projection = commands.add_parser("project", help="Deterministic end-of-month contribution scenarios")
    projection.add_argument("--monthly", type=float, default=20.0)
    projection.add_argument("--years", type=int, nargs="+", default=[10, 20, 30, 40])
    projection.add_argument("--target", type=float, default=1_000_000.0)
    demo = commands.add_parser("make-demo", help="Generate explicitly synthetic monthly data")
    demo.add_argument("--output", type=Path, default=Path("data/synthetic.csv"))
    demo.add_argument("--months", type=int, default=240)
    demo.add_argument("--assets", type=int, default=8)
    demo.add_argument("--seed", type=int, default=212)
    hardware = commands.add_parser("hardware", help="Check optional accelerator availability")
    hardware.add_argument("--torch", action="store_true", help="Import PyTorch to check MPS/CUDA")
    backtest = commands.add_parser("backtest", help="Train and compare with momentum, equal weighting and benchmark")
    backtest.add_argument("--data", type=Path, required=True)
    backtest.add_argument("--output", type=Path, default=Path("runs"))
    backtest.add_argument("--monthly", type=float, default=20.0)
    backtest.add_argument("--model", choices=["ridge", "mlp"], default="ridge")
    backtest.add_argument("--device", choices=["cpu", "auto", "mps", "cuda", "xpu", "hpu"], default="cpu")
    backtest.add_argument("--train-months", type=int, default=60)
    backtest.add_argument("--refit-months", type=int, default=12)
    backtest.add_argument("--holdout-months", type=int, default=24)
    backtest.add_argument("--evaluate-holdout", action="store_true", help="Explicitly evaluate the reserved period; this consumes that holdout")
    backtest.add_argument("--top-k", type=int, default=4)
    backtest.add_argument("--epochs", type=int, default=60)
    backtest.add_argument("--seed", type=int, default=212)
    backtest.add_argument("--fx-bps", type=float, default=15.0)
    backtest.add_argument("--half-spread-bps", type=float, default=5.0)
    backtest.add_argument("--slippage-bps", type=float, default=5.0)
    backtest.add_argument("--buy-tax-bps", type=float, default=0.0)
    backtest.add_argument("--minimum-order", type=float, default=1.0)
    return root


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        if args.command == "project":
            result = {"assumptions": "Zero starting balance; fixed month-end deposits; nominal constant annual effective returns after costs; not forecasts",
                      "monthly_gbp": args.monthly, "target_gbp": args.target, "scenarios": []}
            for years in args.years:
                result["scenarios"].append({"years": years, "at_7_percent_gbp": future_value(args.monthly, years, 0.07),
                                            "at_10_percent_gbp": future_value(args.monthly, years, 0.10),
                                            "required_annual_return": required_return(args.monthly, years, args.target)})
            print(json.dumps(result, indent=2, allow_nan=False))
        elif args.command == "make-demo":
            make_synthetic(args.output, args.months, args.assets, args.seed)
            print(f"Created SYNTHETIC fixture: {args.output}. This is not historical market data.")
        elif args.command == "hardware":
            result = {"system": platform.system(), "architecture": platform.machine(),
                      "python": platform.python_version(), "training_default": "cpu",
                      "backtest_engine": "numpy/cpu; no GPU simulation implemented"}
            if args.torch:
                try:
                    import torch
                    result.update({"torch": torch.__version__, "mps_available": torch.backends.mps.is_available(),
                                   "cuda_available": torch.cuda.is_available(),
                                   "xpu_available": hasattr(torch, "xpu") and torch.xpu.is_available()})
                except ImportError:
                    result["torch"] = "not installed; use uv sync --extra neural"
            print(json.dumps(result, indent=2))
        else:
            config = Experiment(monthly_gbp=args.monthly, model=args.model, device=args.device,
                                train_months=args.train_months, refit_months=args.refit_months,
                                holdout_months=args.holdout_months, evaluate_holdout=args.evaluate_holdout,
                                top_k=args.top_k, epochs=args.epochs, seed=args.seed)
            costs = Costs(args.fx_bps, args.half_spread_bps, args.slippage_bps, args.buy_tax_bps, args.minimum_order)
            destination = run_experiment(load_panel(args.data), config, costs, args.output)
            report = json.loads((destination / "report.json").read_text())
            print(json.dumps({"report": str(destination / "report.json"), "synthetic": report["synthetic"],
                              "scope": report["evaluation_scope"], "device": report["resolved_device"],
                              "summaries": report["summaries"], "limitations": report["limitations"]}, indent=2))
        return 0
    except (ValueError, OSError, ArithmeticError) as exc:
        print(f"twenty: {exc}", file=sys.stderr)
        return 2
