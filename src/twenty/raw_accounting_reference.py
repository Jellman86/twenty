"""Independent exact arithmetic for the authored raw-accounting parity scenarios.

This offline test reference has no market-data, timing, evidence or execution authority.
It supports one original listing, one successor, one dividend and one terminal claim.
The C# ledger remains the application accounting implementation.
"""

from decimal import Decimal, localcontext


def replay(scenario: dict) -> list[dict]:
    """Compute checkpoints from input terms only, ignoring authored expected answers.

    Values must be finite decimal strings. No money rounding is introduced; fixtures
    deliberately avoid the .NET decimal overflow/rounding boundary. Caller's decimal
    context and expected-answer dictionaries cannot affect these calculations.
    """
    with localcontext() as context:
        context.prec = 80
        return _replay(scenario)


def _number(event, key, *, positive=False, nonnegative=False):
    value = event[key]
    if not isinstance(value, str):
        raise TypeError("Decimal strings required")
    try:
        number = Decimal(value)
    except ArithmeticError as exc:
        raise ValueError("Invalid decimal") from exc
    if not number.is_finite() or len(number.as_tuple().digits) > 28 or abs(number.as_tuple().exponent) > 28:
        raise ValueError("Unsupported decimal")
    if positive and number <= 0 or nonnegative and number < 0:
        raise ValueError("Invalid sign")
    return number


def _text(value):
    if value == 0:
        return "0"
    return format(value, "f").rstrip("0").rstrip(".") if value % 1 else format(value, ".0f")


def _replay(scenario):
    if scenario["quoteUnit"] not in ("major", "pence") or scenario["quoteUnit"] == "pence" and scenario["currency"] != "GBP":
        raise ValueError("Unsupported units")
    events = scenario["events"]
    if not isinstance(events, list) or len(events) > 10000:
        raise ValueError("Unsupported event count")
    scale = Decimal("0.01") if scenario["quoteUnit"] == "pence" else Decimal(1)
    cash = contributions = fees = shares = successor = Decimal(0)
    dividend = terminal_cash = terminal_shares = Decimal(0)
    dividend_seen = dividend_paid = terminal_seen = terminal_paid = False
    terminal_kind = None
    equity = None
    checkpoints = []

    def rate(event):
        fx = _number(event, "fx", positive=True)
        if scenario["currency"] == "GBP" and fx != 1:
            raise ValueError("GBP conversion must be unity")
        return fx

    for event in events:
        kind = event["kind"]
        if kind == "deposit":
            amount = _number(event, "amount", positive=True)
            cash += amount
            contributions += amount
        elif kind == "fill":
            quantity = _number(event, "quantity")
            if quantity == 0 or shares + quantity < 0 or terminal_seen:
                raise ValueError("Invalid position")
            fee = _number(event, "fee", nonnegative=True)
            cash -= quantity * _number(event, "price", positive=True) * scale * rate(event) + fee
            shares += quantity
            fees += fee
        elif kind == "split":
            if shares <= 0 or terminal_seen:
                raise ValueError("Missing split position")
            shares *= _number(event, "ratio", positive=True)
        elif kind == "dividend":
            if dividend_seen or shares <= 0:
                raise ValueError("Invalid dividend capture")
            dividend = shares * _number(event, "perShare", positive=True)
            dividend_seen = True
        elif kind == "dividend-payment":
            if not dividend_seen or dividend_paid:
                raise ValueError("Missing unpaid dividend")
            gross = _number(event, "gross", nonnegative=True)
            tax = _number(event, "withheld", nonnegative=True)
            fee = _number(event, "fee", nonnegative=True)
            if gross != dividend or tax > gross:
                raise ValueError("Invalid dividend payment")
            cash += (gross - tax) * rate(event) - fee
            fees += fee
            dividend = Decimal(0)
            dividend_paid = True
        elif kind == "terminal":
            if terminal_seen or shares <= 0:
                raise ValueError("Invalid terminal capture")
            terminal_kind = event["outcome"]
            if terminal_kind == "cash":
                terminal_cash = shares * _number(event, "perShare", positive=True)
            elif terminal_kind == "stock":
                terminal_shares = shares * _number(event, "ratio", positive=True)
            elif terminal_kind == "zero":
                terminal_paid = True
            else:
                raise ValueError("Unsupported terminal outcome")
            shares = Decimal(0)
            terminal_seen = True
        elif kind == "terminal-settlement":
            if not terminal_seen or terminal_paid:
                raise ValueError("Missing unsettled terminal")
            fee = _number(event, "fee", nonnegative=True)
            if terminal_kind == "stock":
                if _number(event, "quantity", positive=True) != terminal_shares:
                    raise ValueError("Invalid share delivery")
                successor += terminal_shares
                terminal_shares = Decimal(0)
                cash -= fee
            else:
                gross = _number(event, "gross", positive=True)
                tax = _number(event, "withheld", nonnegative=True)
                if gross != terminal_cash or tax > gross:
                    raise ValueError("Invalid terminal payment")
                cash += (gross - tax) * rate(event) - fee
                terminal_cash = Decimal(0)
            fees += fee
            terminal_paid = True
        elif kind == "mark":
            if dividend_seen and not dividend_paid or terminal_seen and not terminal_paid:
                raise ValueError("Pending claims require a separate valuation model")
            equity = cash + rate(event) * (
                shares * _number(event, "price", positive=True) * scale
                + successor * _number(event, "successorPrice", positive=True)
            )
        else:
            raise ValueError("Unsupported operation")
        if cash < 0:
            raise ValueError("Insufficient cash")
        checkpoints.append({
            "cash": _text(cash), "contributions": _text(contributions), "fees": _text(fees),
            "quantity": _text(shares), "successorQuantity": _text(successor),
            "pendingDividendLocal": _text(dividend), "pendingTerminalLocal": _text(terminal_cash),
            "pendingSuccessorQuantity": _text(terminal_shares),
            "equity": None if equity is None else _text(equity),
        })
    return checkpoints
