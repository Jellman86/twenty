"""Independently compare converted bars/actions with private captured Tiingo payloads."""
import argparse
import hashlib
import json
import re
from collections import Counter, defaultdict
from datetime import UTC, datetime, time
from decimal import Decimal
from pathlib import Path
from urllib.parse import urlsplit
from zoneinfo import ZoneInfo


def read_json(path):
    return json.loads(Path(path).read_bytes(), parse_float=Decimal)


def instant(value):
    return datetime.fromisoformat(value)


def check_equal(actual, expected, label):
    if actual != expected:
        raise ValueError(label)


def capture_symbol(record, bucket):
    """The recorded request disambiguates old dot-to-hyphen source buckets."""
    uri = urlsplit(record["url"])
    match = re.fullmatch(r"/tiingo/daily/([A-Za-z][A-Za-z0-9.-]{0,19})/prices", uri.path)
    if (uri.scheme != "https" or uri.hostname != "api.tiingo.com" or uri.port not in (None, 443)
            or uri.username is not None or uri.password is not None or uri.fragment or not match):
        raise ValueError("capture request identity")
    symbol = match[1].lower()
    legacy = "tiingo-history-" + symbol.replace(".", "-")
    canonical = ("tiingo-history-v2-" + symbol.replace("-", "-h").replace(".", "-d")
                 if "." in symbol or "-" in symbol else "tiingo-history-" + symbol)
    if record["sourceId"] != bucket or bucket not in (legacy, canonical):
        raise ValueError("capture source identity")
    return symbol


def audit(dataset, captures, schedule=None, schedule_sha=None):
    """captures maps (ticker, capture instant) to its parsed producer rows."""
    intervals = defaultdict(list)
    for observation in dataset["observations"]:
        if observation["kind"] == "listing":
            intervals[observation["listingId"]].append(observation)
    listings = {key: rows[0] for key, rows in intervals.items()}
    calendars = {c["id"]: c for c in dataset["manifest"]["calendars"]}

    def session_close(listing, date):
        calendar = calendars[listing["calendarId"]]
        day = datetime.fromisoformat(date).date()
        hour = 16
        version = calendar.get("version", "unverified-v1")
        if version != "unverified-v1":
            if schedule is None or version != schedule["version"]:
                raise ValueError("unsupported calendar version")
            check_equal(calendar["contentSha256"], schedule_sha, "calendar checksum")
            check_equal(calendar["timeZone"], schedule["timeZone"], "calendar timezone")
            check_equal(listing["venue"], schedule["venue"], "calendar venue")
            year = next((y for y in schedule["years"] if y["year"] == day.year), None)
            if year is None:
                raise ValueError("unsupported calendar year")
            if day.weekday() >= 5 or date in year["closed"]:
                raise ValueError("non-session producer row")
            hour = 13 if date in year["earlyCloses"] else 16
        return datetime.combine(day, time(hour), ZoneInfo(calendar["timeZone"]))
    counts = Counter()
    checked = set()
    expected = set()
    expected_actions = set()
    checked_actions = set()
    for listing_id, listing in listings.items():
        ticker = next(p["value"] for p in listing["providerIds"] if p["namespace"] == "tiingo.ticker")
        action_series = set()
        for (symbol, captured), rows in sorted(captures.items()):
            if symbol != ticker.lower():
                continue
            for date, row in rows.items():
                close = session_close(listing, date)
                active = any(row["status"] == "active" and instant(row["effectiveFrom"]) <= close and (
                    row["effectiveUntil"] is None or close < instant(row["effectiveUntil"])
                ) for row in intervals[listing_id])
                if active and close <= captured:
                    for basis in ("raw", "totalReturnAdjusted"):
                        expected.add((listing_id, date, captured, basis))
                    for action, present in (("dividend", row["divCash"] > 0), ("split", row["splitFactor"] != 1)):
                        if present or (date, action) in action_series:
                            action_series.add((date, action))
                            expected_actions.add((listing_id, date, captured, action))
    for observation in dataset["observations"]:
        kind = observation["kind"]
        if kind not in ("priceBar", "corporateAction"):
            continue
        listing = listings[observation["listingId"]]
        ticker = next(p["value"] for p in listing["providerIds"] if p["namespace"] == "tiingo.ticker")
        evidence = observation["evidence"]
        captured = instant(evidence["availableAt"])
        check_equal(instant(evidence["publishedAt"]), captured, "publication/capture mismatch")
        check_equal(evidence["availabilityEvidence"], "observedPublication", "availability evidence")
        date = observation.get("sessionDate") or instant(observation["effectiveAt"]).astimezone(
            ZoneInfo(calendars[listing["calendarId"]]["timeZone"])).date().isoformat()
        row = captures[(ticker.lower(), captured)][date]
        close = session_close(listing, date).astimezone(UTC)
        if close > captured:
            raise ValueError("incomplete session emitted")
        if kind == "priceBar":
            check_equal(instant(observation["sessionCloseAt"]), close, "session close/DST mismatch")
            check_equal(observation["denomination"], {"currency": "USD", "unit": "major"}, "denomination")
            adjusted = observation["basis"] == "totalReturnAdjusted"
            for field in ("open", "high", "low", "close", "volume"):
                producer_field = "adj" + field.capitalize() if adjusted else field
                check_equal(observation[field], row[producer_field], "producer " + producer_field + " mismatch")
            key = (observation["listingId"], date, captured, observation["basis"])
            if key in checked:
                raise ValueError("duplicate converted bar")
            checked.add(key)
            counts[observation["basis"]] += 1
        else:
            check_equal(instant(observation["effectiveAt"]), close, "action effective instant")
            action = observation["action"]
            if action == "dividend":
                check_equal(observation["cashPerShare"], row["divCash"], "dividend mismatch")
                check_equal(observation["cashCurrency"], "USD", "dividend denomination")
            elif action == "split":
                check_equal(observation["splitRatio"], row["splitFactor"], "split mismatch")
            else:
                raise ValueError("unexpected converted action")
            key = (observation["listingId"], date, captured, action)
            if key in checked_actions:
                raise ValueError("duplicate converted action")
            checked_actions.add(key)
            counts[action] += 1
    check_equal(checked_actions, expected_actions, "missing or unexpected converted actions")
    check_equal(checked, expected, "missing or unexpected converted bars")
    return {"valid": True, "counts": dict(sorted(counts.items())), "checkedBars": len(checked)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset", required=True, type=Path)
    parser.add_argument("--captures", required=True, type=Path)
    args = parser.parse_args()
    captures = {}
    provenance = []
    root = args.captures / "capture-v1"
    for path in sorted((root / "observations").glob("tiingo-history-*/*.jsonl")):
        for line in path.read_text().splitlines():
            record = json.loads(line)
            payload = (root / "objects" / (record["sha256"] + ".bin")).read_bytes()
            check_equal(hashlib.sha256(payload).hexdigest(), record["sha256"], "capture checksum")
            key = (capture_symbol(record, path.parent.name), instant(record["observedAt"]))
            rows = {row["date"][:10]: row for row in json.loads(payload, parse_float=Decimal)}
            if key in captures:
                check_equal(captures[key], rows, "same-instant capture conflict")
            captures[key] = rows
            provenance.append({k: record[k] for k in ("sourceId", "observedAt", "sha256")})
    calendar_path = Path(__file__).resolve().parent.parent / "src/Twenty.Ingestion/Listings/nasdaq-equity-sessions.v1.json"
    result = audit(read_json(args.dataset), captures, read_json(calendar_path),
                   hashlib.sha256(calendar_path.read_bytes()).hexdigest())
    result.update({"auditVersion": "listing-prices-wire-v3",
                   "datasetSha256": hashlib.sha256(args.dataset.read_bytes()).hexdigest(),
                   "captures": provenance,
                   "limitations": "Same-producer wire comparison; timestamps checked independently against the shared reviewed session table; not independent economic or calendar-source validation."})
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
