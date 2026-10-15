"""Deterministic scenarios, not market forecasts."""

import math


def future_value(monthly: float, years: int, annual_return: float) -> float:
    if not math.isfinite(monthly) or monthly < 0 or years < 0:
        raise ValueError("Contributions and years must be non-negative")
    if not math.isfinite(annual_return) or annual_return <= -1:
        raise ValueError("Annual return must be finite and greater than -100%")
    if annual_return == 0:
        return monthly * years * 12
    rate = math.expm1(math.log1p(annual_return) / 12)
    return monthly * math.expm1(years * math.log1p(annual_return)) / rate


def required_return(monthly: float, years: int, target: float) -> float:
    if monthly <= 0 or years <= 0 or not math.isfinite(target) or target <= 0:
        raise ValueError("Positive contribution, years and target required")
    low, high = -0.999999, 1.0
    while future_value(monthly, years, high) < target:
        high *= 2
        if high > 1024:
            raise ValueError("Target exceeds the supported scenario range")
    for _ in range(120):
        middle = (low + high) / 2
        if future_value(monthly, years, middle) < target:
            low = middle
        else:
            high = middle
    return (low + high) / 2
