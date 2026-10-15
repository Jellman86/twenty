"""Fractional GBP-notional research simulator; not a broker-fill simulator."""

import math
from dataclasses import asdict, dataclass, field


@dataclass(frozen=True)
class Costs:
    fx_bps: float = 15.0
    half_spread_bps: float = 5.0
    slippage_bps: float = 5.0
    buy_tax_bps: float = 0.0
    minimum_order_gbp: float = 1.0

    def __post_init__(self) -> None:
        if any(not math.isfinite(value) or value < 0 for value in asdict(self).values()):
            raise ValueError("Cost assumptions must be finite and non-negative")
        if self.sell_rate >= 1 or self.buy_rate >= 1:
            raise ValueError("One-way cost rate must be less than 100%")

    @property
    def sell_rate(self) -> float:
        return (self.fx_bps + self.half_spread_bps + self.slippage_bps) / 10_000

    @property
    def buy_rate(self) -> float:
        return self.sell_rate + self.buy_tax_bps / 10_000


@dataclass
class Portfolio:
    cash: float = 0.0
    holdings: dict[str, float] = field(default_factory=dict)
    contributions: float = 0.0
    costs_paid: float = 0.0
    traded_notional: float = 0.0
    unit_price: float = 1.0
    high_water: float = 1.0
    max_drawdown: float = 0.0

    @property
    def equity(self) -> float:
        return self.cash + sum(self.holdings.values())

    def step(self, monthly: float, weights: dict[str, float], returns: dict[str, float], costs: Costs) -> dict:
        if not math.isfinite(monthly) or monthly < 0:
            raise ValueError("Contribution must be finite and non-negative")
        if any(not math.isfinite(w) or w < 0 for w in weights.values()) or sum(weights.values()) > 1 + 1e-10:
            raise ValueError("Long-only weights must sum to at most one")
        if any(not math.isfinite(r) or r < -1 for r in returns.values()):
            raise ValueError("Returns must be finite and at least -100%")
        if not (set(weights) | set(self.holdings)) <= returns.keys():
            raise ValueError("Missing asset returns; refusing to invent prices")
        self.cash += monthly
        self.contributions += monthly
        before = self.equity
        target = {symbol: before * weight for symbol, weight in weights.items()}
        previous_cost = self.costs_paid
        previous_traded = self.traded_notional
        # Sell first; each buy must be affordable including its own costs.
        for symbol, held in tuple(self.holdings.items()):
            amount = max(0.0, held - target.get(symbol, 0.0))
            if amount > 1e-9 and amount + 1e-10 >= costs.minimum_order_gbp:
                fee = amount * costs.sell_rate
                self.holdings[symbol] -= amount
                self.cash += amount - fee
                self.costs_paid += fee
                self.traded_notional += amount
        wanted = {symbol: max(0.0, amount - self.holdings.get(symbol, 0.0)) for symbol, amount in target.items()}
        wanted = {symbol: amount for symbol, amount in wanted.items() if amount > 1e-9 and amount + 1e-10 >= costs.minimum_order_gbp}
        needed = sum(wanted.values()) * (1 + costs.buy_rate)
        scale = min(1.0, max(0.0, self.cash) / needed) if needed else 0.0
        for symbol, wanted_amount in wanted.items():
            amount = wanted_amount * scale
            if amount + 1e-10 < costs.minimum_order_gbp:
                continue
            fee = amount * costs.buy_rate
            self.cash -= amount + fee
            self.holdings[symbol] = self.holdings.get(symbol, 0.0) + amount
            self.costs_paid += fee
            self.traded_notional += amount
        self.holdings = {symbol: amount * (1 + returns[symbol]) for symbol, amount in self.holdings.items() if amount > 1e-12}
        self.cash = max(0.0, self.cash) if self.cash > -1e-8 else self.cash
        if self.cash < 0:
            raise ArithmeticError("Simulator attempted to borrow")
        period_return = self.equity / before - 1 if before else 0.0
        self.unit_price *= 1 + period_return
        self.high_water = max(self.high_water, self.unit_price)
        self.max_drawdown = min(self.max_drawdown, self.unit_price / self.high_water - 1)
        return {"equity_gbp": self.equity, "cash_gbp": self.cash,
                "contributions_gbp": self.contributions, "period_return": period_return,
                "period_cost_gbp": self.costs_paid - previous_cost,
                "traded_notional_gbp": self.traded_notional - previous_traded,
                "unit_price": self.unit_price, "max_drawdown": self.max_drawdown}
