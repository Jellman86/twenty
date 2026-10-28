"""Rendered UI workflow check for the operational Twenty web app.

Run against a private, loopback-only API (TWENTY_AUTH=off) whose data root is a
fresh copy of an authored fixture, never production:

    uv run --no-project --with playwright python scripts/ui_workflow_check.py \
        --base http://127.0.0.1:18791 --out <new evidence dir> [--create-fixture <create.json>]

Broker, journal and simulated-ledger responses are SYNTHETIC intercepted HTTP
fixtures for engineering only; they are not genuine holdings and make no broker
call. Raw paper account checks use the real backend responses, and --create-fixture
enables one real authored account creation (stress costs, so it needs a fresh copy
of the sealed fixture) and one prepare/apply in that copy.
"""

# ruff: noqa: B023  # each check lambda is invoked immediately by Checks.run, inside its loop iteration

from __future__ import annotations

import argparse
import copy
import json
import re
import sys
from pathlib import Path

from playwright.sync_api import Page, Route, sync_playwright

SYNTHETIC_PORTFOLIO = {
    "broker": {
        "environment": "demo",
        "account": "…4821",
        "currency": "GBP",
        "availableCash": 152.37,
        "reservedCash": 10,
        "holdings": [
            {"symbol": "AAPL_US_EQ", "name": "Apple Inc.", "quantity": 0.5, "averagePrice": 190.0, "currentPrice": 175.0,
             "priceCurrency": "USD", "costValue": 75.40, "currentValue": 69.10, "unrealizedProfitLoss": -6.30,
             "valueCurrency": "GBP", "managedQuantity": 0},
            {"symbol": "LLOYl_EQ", "name": "Lloyds Banking Group", "quantity": 100, "averagePrice": 48.2, "currentPrice": 52.1,
             "priceCurrency": "GBX", "costValue": 48.20, "currentValue": 52.10, "unrealizedProfitLoss": 3.90,
             "valueCurrency": "GBP", "managedQuantity": 40},
        ],
        "totalCost": 123.60,
        "totalValue": 121.20,
    },
    "brokerError": None,
    "brokerConfigured": True,
    "simulated": [
        {"id": "sim-1", "runId": "0f1e2d3c4b5a", "synthetic": True, "contributionsGbp": 40, "cashGbp": 1.25, "reservedGbp": 0,
         "holdings": [{"symbol": "AAA", "quantity": 2, "costGbp": 38.75, "averagePriceGbp": 19.375}], "openOrders": 0,
         "lastMonth": "2026-09"},
    ],
}
SYNTHETIC_PARTIAL = {
    **SYNTHETIC_PORTFOLIO,
    "broker": {
        **SYNTHETIC_PORTFOLIO["broker"],
        "holdings": [*SYNTHETIC_PORTFOLIO["broker"]["holdings"],
                     {"symbol": "XYZ_EQ", "name": None, "quantity": 1, "averagePrice": None, "currentPrice": None,
                      "priceCurrency": None, "costValue": None, "currentValue": None, "unrealizedProfitLoss": None,
                      "valueCurrency": None, "managedQuantity": 0}],
        "totalCost": None,
        "totalValue": None,
    },
}
SYNTHETIC_BROKER = {
    "configured": True, "enabled": True, "automatic": False, "environment": "demo", "monthlyAllowanceGbp": 20,
    "credentialsConfigured": True, "paused": False, "codeSha256": "0" * 64, "configurationError": None, "demoReceipts": [],
    "accounts": [{
        "key": "demo-4821", "environment": "demo", "account": "…4821", "allocatedCapitalGbp": 40, "cashGbp": 0.72,
        "reservedGbp": 19.9, "holdings": {"LLOY": 40}, "blockCode": None,
        "batches": [{"id": "b1", "month": "2026-09", "createdAt": "2026-09-01T09:00:00+00:00"},
                    {"id": "b2", "month": "2026-10", "createdAt": "2026-10-01T09:00:00+00:00"}],
        "intents": [
            {"id": "i1", "batchId": "b1", "phase": "filled", "brokerOrderId": 101, "reportedFilled": 40, "errorCode": None,
             "submittedAt": "2026-09-01T09:01:00+00:00",
             "intent": {"symbol": "LLOY", "brokerTicker": "LLOYl_EQ", "quantity": 40, "limitPrice": 49, "quoteCurrency": "GBX",
                        "maximumDebitGbp": 19.9, "maximumFeeBps": 0},
             "progress": {"requested": 40, "filled": 40, "debitGbp": 19.28, "reservedGbp": 19.9, "status": "filled", "remainingReserve": 0}},
            {"id": "i2", "batchId": "b2", "phase": "pending", "brokerOrderId": 102, "reportedFilled": 0, "errorCode": None,
             "submittedAt": "2026-10-01T09:01:00+00:00",
             "intent": {"symbol": "AAPL", "brokerTicker": "AAPL_US_EQ", "quantity": 0.1, "limitPrice": 176, "quoteCurrency": "USD",
                        "maximumDebitGbp": 19.9, "maximumFeeBps": 15},
             "progress": {"requested": 0.1, "filled": 0, "debitGbp": 0, "reservedGbp": 19.9, "status": "pending", "remainingReserve": 19.9}},
        ],
    }],
}
SYNTHETIC_PAPER = {"mode": "simulation", "paused": False, "accounts": [{
    "id": "sim-1", "runId": "0f1e2d3c4b5a", "cashGbp": 1.25, "contributionsGbp": 40, "quantities": {"AAA": 2},
    "decisions": [{"month": "2026-09", "signalDate": "2026-08-31", "weights": {"AAA": 1}, "notes": []}],
    "orders": [{"id": "o1", "period": "2026-09", "intent": {"symbol": "AAA", "quantity": 2, "limitPrice": 19, "maximumDebitGbp": 38.75},
                "progress": {"status": "filled", "filled": 2, "debitGbp": 38.75, "remainingReserve": 0}}]}]}

ROUTES = ["#portfolio", "#activity", "#execution", "#paper", "#research", "#settings"]


class Checks:
    def __init__(self) -> None:
        self.results: list[dict[str, object]] = []

    def add(self, name: str, ok: bool, detail: object = None) -> None:
        self.results.append({"name": name, "ok": bool(ok), "detail": detail})
        print(("PASS " if ok else "FAIL ") + name + (f" :: {detail}" if detail is not None and not ok else ""))

    def run(self, name: str, fn) -> None:
        try:
            ok, detail = fn()
            self.add(name, ok, detail)
        except Exception as error:  # noqa: BLE001 - every failure is recorded, not hidden
            self.add(name, False, f"{type(error).__name__}: {error}"[:400])


def fulfil(body: object, status: int = 200):
    def handler(route: Route) -> None:
        if route.request.method != "GET":
            route.abort()
            return
        route.fulfill(status=status, content_type="application/json", body=json.dumps(body))
    return handler


def mock_broker(page: Page, portfolio: object = SYNTHETIC_PORTFOLIO, status: int = 200) -> None:
    page.route("**/api/portfolio", fulfil(portfolio, status))
    page.route("**/api/execution/broker", fulfil(SYNTHETIC_BROKER))
    page.route("**/api/execution/paper", fulfil(SYNTHETIC_PAPER))


def no_overflow(page: Page) -> bool:
    return page.evaluate("document.documentElement.scrollWidth <= window.innerWidth + 1")


def text(page: Page) -> str:
    return page.locator("main").inner_text(timeout=5000)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--create-fixture")
    args = parser.parse_args()
    if not re.match(r"^http://127\.0\.0\.1:\d+$", args.base):
        raise SystemExit("Refusing a non-loopback API base; use a private fixture copy.")
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=False)
    checks = Checks()
    posts: list[str] = []
    with sync_playwright() as p:
        browser = p.chromium.launch()

        def new_page(width: int) -> Page:
            page = browser.new_page(viewport={"width": width, "height": 900})
            page.on("pageerror", lambda e: checks.add("no page error", False, str(e)[:300]))
            page.on("request", lambda r: posts.append(f"{r.method} {r.url}") if r.method != "GET" and "/api/" in r.url else None)
            return page

        for width in (1280, 390):
            page = new_page(width)
            mock_broker(page)
            posts.clear()
            page.goto(args.base + "/")
            page.wait_for_selector("main h1")
            checks.run(f"{width}: default landing is Portfolio", lambda: (page.locator("main h1").first.inner_text() == "Portfolio", page.locator("main h1").first.inner_text()))
            page.get_by_text("Apple Inc.").first.wait_for(timeout=5000)
            body = text(page)
            checks.run(f"{width}: holdings show company names and tickers", lambda: (all(s in body for s in ["Apple Inc.", "AAPL_US_EQ", "Lloyds Banking Group", "LLOYl_EQ"]), body[:600]))
            checks.run(f"{width}: GBX quote kept in pence, USD in dollars", lambda: ("52.10 GBX" in body and "US$175.00" in body, None))
            checks.run(f"{width}: total value, cash, signed gain", lambda: ("£121.20" in body and "£152.37" in body and "−£2.40" in body and "+£3.90" in body and "−£6.30" in body, None))
            checks.run(f"{width}: negative gain styled as loss", lambda: (page.locator(".loss", has_text="−£6.30").count() > 0, None))
            checks.run(f"{width}: app-managed shares separated from external", lambda: (page.get_by_text("40 via Twenty").count() == 1 and "via Twenty" not in page.locator("tr", has_text="Apple Inc.").inner_text(), None))
            checks.run(f"{width}: demo account labelled", lambda: (page.locator("[data-account-kind=demo]").count() > 0, None))
            checks.run(f"{width}: no research/diagnostic narrative on Portfolio", lambda: (not re.search(r"What we have bought|\bSHA(-256)?\b|UUID|Speculative growth|[0-9a-f]{32,64}", body), None))
            checks.run(f"{width}: portfolio has no horizontal overflow", lambda: (no_overflow(page), None))
            checks.run(f"{width}: every main navigation link is visible without scrolling", lambda: (page.evaluate("""[...document.querySelectorAll('nav[aria-label=Main] a')].every(a => {
                const r = a.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth && r.width > 0;
            })"""), None))
            page.screenshot(path=str(out / f"portfolio-{width}.png"), full_page=True)
            page.get_by_role("button", name=re.compile("Simulated")).click()
            sim = text(page)
            checks.run(f"{width}: simulated account switch shows booked cost, not value", lambda: ("AAA" in sim and "£38.75" in sim and "Apple Inc." not in sim, sim[:300]))
            page.screenshot(path=str(out / f"portfolio-simulated-{width}.png"), full_page=True)

            page.goto(args.base + "/#activity")
            page.wait_for_selector("main h1:has-text('Activity')", timeout=5000)
            page.get_by_text("LLOY").first.wait_for(timeout=5000)
            act = text(page)
            checks.run(f"{width}: activity lists fills and statuses", lambda: (all(s in act for s in ["LLOY", "Filled", "£19.28", "AAPL", "Open", "AAA"]), act[:600]))
            checks.run(f"{width}: activity has no horizontal overflow", lambda: (no_overflow(page), None))
            page.screenshot(path=str(out / f"activity-{width}.png"), full_page=True)

            for route in ROUTES:
                page.goto(args.base + "/" + route)
                page.wait_for_selector("main h1", timeout=8000)
                page.wait_for_timeout(600)
                checks.run(f"{width}: {route} has no horizontal overflow", lambda: (no_overflow(page), page.evaluate("document.documentElement.scrollWidth")))
                checks.run(f"{width}: {route} nav marks current page", lambda: (page.locator("nav[aria-label=Main] a[aria-current=page]").count() == 1, None))
                page.screenshot(path=str(out / f"route-{route[1:]}-{width}.png"), full_page=True)
            checks.run(f"{width}: loading these pages issued no side-effecting request", lambda: (not posts, posts))
            page.close()

        # Reconciliation phases remain visible even after some shares have filled.
        # A cancellation request is an actual broker-journal phase, not a completed cancellation.
        review_broker = copy.deepcopy(SYNTHETIC_BROKER)
        partial = review_broker["accounts"][0]["intents"][0]
        partial.update(id="cancel-partial", phase="cancel_requested")
        partial["progress"].update(filled=10, debitGbp=4.82, status="partially_filled", remainingReserve=15.08)
        unknown = copy.deepcopy(partial)
        unknown.update(id="unknown-partial", phase="unknown", errorCode="broker-reconciliation-required")
        unknown["intent"]["symbol"] = "CHECK"
        review_broker["accounts"][0]["intents"].append(unknown)
        review_broker["accounts"][0]["intents"][1]["phase"] = "cancel_requested"
        page = new_page(390)
        page.route("**/api/execution/broker", fulfil(review_broker))
        page.route("**/api/execution/paper", fulfil(SYNTHETIC_PAPER))
        page.goto(args.base + "/#activity")
        page.get_by_text("CHECK", exact=True).wait_for(timeout=5000)
        checks.run("partial fill does not hide cancellation request", lambda: ("Cancellation requested" in page.locator("tr", has_text="LLOY").inner_text(), None))
        checks.run("partial fill does not hide unknown order state", lambda: ("Needs check" in page.locator("tr", has_text="CHECK").inner_text(), None))
        page.get_by_role("button", name="Open", exact=True).click()
        checks.run("open activity includes cancellation and reconciliation", lambda: (page.get_by_text("LLOY", exact=True).count() == 1 and page.get_by_text("CHECK", exact=True).count() == 1, None))
        checks.run("unfilled cancellation remains in open activity", lambda: (page.get_by_text("AAPL", exact=True).count() == 1, None))
        page.screenshot(path=str(out / "activity-reconciliation-390.png"), full_page=True)
        page.close()

        page = new_page(1280)
        for old in ["#workbench", "#roadmap", "#evidence", "#unknown", ""]:
            page.goto(args.base + "/" + old)
            page.wait_for_selector("main h1", timeout=8000)
            heading = page.locator("main h1").first.inner_text()
            expected = {"#workbench": "Scenarios", "#roadmap": "Roadmap", "#evidence": "Evidence"}.get(old, "Portfolio")
            checks.run(f"deep link '{old or '/'}' resolves", lambda: (heading == expected, heading))
        page.close()

        page = new_page(1280)
        mock_broker(page, SYNTHETIC_PARTIAL)
        page.goto(args.base + "/#portfolio")
        page.get_by_text("XYZ_EQ").first.wait_for(timeout=5000)
        row = page.locator("tr", has_text="XYZ_EQ").inner_text()
        checks.run("missing broker values show unavailable, never zero", lambda: ("£0.00" not in row and "—" in row, row))
        checks.run("partial totals withheld", lambda: ("£121.20" not in text(page), None))
        page.close()

        for portfolio, status, expect in [
            ({**SYNTHETIC_PORTFOLIO, "broker": None, "brokerError": "broker-auth-rejected"}, 200, "rejected the API key"),
            (None, 500, "Portfolio unavailable"),
            ({"broker": None, "brokerError": None, "brokerConfigured": False, "simulated": []}, 200, "No broker connected"),
        ]:
            page = new_page(390)
            mock_broker(page, portfolio, status)
            page.goto(args.base + "/#portfolio")
            page.wait_for_timeout(1200)
            checks.run(f"portfolio state: {expect}", lambda: (expect in text(page), text(page)[:300]))
            page.screenshot(path=str(out / f"portfolio-state-{re.sub('[^a-z]+', '-', expect.lower())}.png"), full_page=True)
            page.close()

        # Real backend: authored raw paper account (fixture copy).
        for width in (1280, 390):
            page = new_page(width)
            receipts: list[str] = []
            page.on("request", lambda r: receipts.append(r.url) if "/receipt" in r.url else None)
            page.goto(args.base + "/#paper")
            page.wait_for_selector("main h1:has-text('Paper accounts')", timeout=8000)
            page.get_by_text("£39.50").first.wait_for(timeout=8000)
            paper = text(page)
            checks.run(f"{width}: real paper account shows £39.50 equity and 0.9875 shares", lambda: ("£39.50" in paper and "0.9875" in paper, paper[:500]))
            checks.run(f"{width}: pure-split mode labelled", lambda: ("Stock splits" in paper, None))
            visible = page.evaluate("(() => { const m = document.querySelector('main').cloneNode(true); m.querySelectorAll('details').forEach(d => d.remove()); return m.innerText; })()")
            checks.run(f"{width}: no hashes or UUIDs on paper main screen", lambda: (not re.search(r"[0-9a-f]{32,64}", visible), re.findall(r"[0-9a-f]{32,64}", visible)[:3]))
            checks.run(f"{width}: receipts not fetched automatically", lambda: (not receipts, receipts))
            checks.run(f"{width}: paper has no horizontal overflow", lambda: (no_overflow(page), None))
            page.screenshot(path=str(out / f"paper-{width}.png"), full_page=True)
            page.close()

        page = new_page(1280)
        page.route("**/api/research/raw-paper-accounts", lambda r: r.fulfill(status=200, content_type="application/json", body=json.dumps({
            "accounts": [], "preparationAvailable": True, "authoredOnly": True})) if r.request.method == "GET" else r.continue_())
        page.goto(args.base + "/#paper")
        page.wait_for_selector("main h1:has-text('Paper accounts')", timeout=8000)
        page.wait_for_timeout(800)
        page.get_by_text("New paper account").click()
        checks.run("older server without pureSplitsAvailable hides split opt-in", lambda: (page.get_by_label(re.compile("stock splits", re.IGNORECASE)).count() == 0, None))
        page.close()

        if args.create_fixture:
            create = json.loads(Path(args.create_fixture).read_text())
            page = new_page(1280)
            bodies: list[dict] = []
            page.on("request", lambda r: bodies.append(json.loads(r.post_data or "{}")) if r.method == "POST" and r.url.endswith("/api/research/raw-paper-accounts") else None)
            refusal = {"armed": True}

            def refuse_once(route: Route) -> None:
                if route.request.method == "POST" and refusal["armed"]:
                    refusal["armed"] = False
                    route.fulfill(status=409, content_type="application/json", body=json.dumps({"error": "raw-paper-already-created"}))
                else:
                    route.continue_()
            page.route("**/api/research/raw-paper-accounts", refuse_once)
            page.goto(args.base + "/#paper")
            page.wait_for_selector("main h1:has-text('Paper accounts')", timeout=8000)
            page.get_by_text("£39.50").first.wait_for(timeout=8000)
            page.get_by_text("New paper account").click()
            page.get_by_role("combobox", name="Costs").select_option("stress")
            page.get_by_label("Dataset SHA-256").fill(create["datasetSha256"])
            page.get_by_label("Calendar pins").fill(json.dumps(create["calendars"]))
            page.get_by_label("Decision dates").fill(json.dumps(create["decisions"]))
            page.get_by_role("button", name="Create account").click()
            page.wait_for_timeout(800)
            checks.run("default creation omits actionMode (intercepted refusal)", lambda: (bodies and "actionMode" not in bodies[-1], bodies[-1:] and list(bodies[-1].keys())))
            checks.run("refusal shown as concise message", lambda: ("already has a paper account" in text(page), None))
            page.get_by_label(re.compile("stock splits", re.IGNORECASE)).check()
            page.get_by_role("button", name="Create account").click()
            page.get_by_text("stress costs").first.wait_for(timeout=15000)
            checks.run("opt-in creation sends pure-splits-v1 (real backend)", lambda: (bodies[-1].get("actionMode") == "pure-splits-v1", bodies[-1].get("actionMode")))
            page.screenshot(path=str(out / "paper-created-1280.png"), full_page=True)
            # Return to the original account and run one prepare/apply.
            page.get_by_role("button", name=re.compile("base costs")).click()
            page.get_by_role("button", name="Prepare next batch").click()
            page.get_by_text("Projected").first.wait_for(timeout=60000)
            projected = text(page)
            checks.run("prepared batch shown as projected; accepted equity unchanged", lambda: ("Not yet applied" in projected and "2 periods" in projected, projected[:800]))
            page.screenshot(path=str(out / "paper-prepared-1280.png"), full_page=True)
            page.get_by_role("button", name="Apply batch").click()
            page.get_by_text("3 periods").first.wait_for(timeout=60000)
            checks.run("apply records the batch (3 accepted periods)", lambda: ("3 periods" in text(page) and "Not yet applied" not in text(page), None))
            page.screenshot(path=str(out / "paper-applied-1280.png"), full_page=True)
            page.close()

        page = new_page(390)
        page.route("**/api/auth/state", fulfil({"authenticated": False, "username": None, "setupRequired": False, "passwordChangedAt": None}))
        page.goto(args.base + "/")
        page.wait_for_selector("form", timeout=5000)
        checks.run("login screen renders", lambda: (page.get_by_role("button", name="Sign in").count() == 1 and no_overflow(page), None))
        page.screenshot(path=str(out / "login-390.png"), full_page=True)
        page.close()

        page = new_page(1280)
        state = {"auth": True}
        page.route("**/api/auth/state", lambda r: r.fulfill(status=200, content_type="application/json", body=json.dumps(
            {"authenticated": state["auth"], "username": "operator", "setupRequired": False, "passwordChangedAt": None})))
        page.route("**/api/portfolio", lambda r: r.fulfill(status=401, body="{}"))
        page.goto(args.base + "/#portfolio")
        state["auth"] = False
        page.wait_for_timeout(1500)
        checks.run("401 returns to sign-in", lambda: (page.get_by_role("button", name="Sign in").count() == 1, None))
        page.close()
        browser.close()

    failed = [c for c in checks.results if not c["ok"]]
    (out / "ui-checks.json").write_text(json.dumps({"base": args.base, "synthetic": "broker/journal/simulated responses are intercepted synthetic fixtures",
                                                     "passed": len(checks.results) - len(failed), "failed": len(failed), "results": checks.results}, indent=2))
    print(f"{len(checks.results) - len(failed)} passed, {len(failed)} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
