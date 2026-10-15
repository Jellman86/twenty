"""Exercise the real HTTP contract, including invalid input and static routes."""
import json
import sys
import urllib.error
import urllib.request

base = sys.argv[1].rstrip("/")

def get(path):
    return urllib.request.urlopen(base + path, timeout=10)

assert json.load(get("/health/ready"))["mode"] == "research"
assert json.load(get("/api/meta"))["brokerConnected"] is False
assert len(json.load(get("/api/roadmap"))) == 11
assert b'<div id="app">' in get("/").read()
request = urllib.request.Request(base + "/api/projections", json.dumps({"monthly":20,"years":30,"annualReturn":0.07,"target":1000000}).encode(), {"Content-Type": "application/json"})
result = json.load(urllib.request.urlopen(request, timeout=10))
assert result["points"][-1]["contributions"] == 7200
assert 23000 < result["points"][-1]["balance"] < 24000
assert 0.25 < result["requiredAnnualReturn"] < 0.27
for path, payload, expected in [("/api/missing", None, 404), ("/api/projections", {"monthly":-1,"years":30,"annualReturn":0.07}, 400)]:
    req = urllib.request.Request(base + path, json.dumps(payload).encode() if payload else None, {"Content-Type": "application/json"})
    try:
        urllib.request.urlopen(req, timeout=10)
        raise AssertionError(f"Expected {expected} for {path}")
    except urllib.error.HTTPError as error:
        assert error.code == expected
print("HTTP smoke passed: readiness, metadata, roadmap, SPA, scenario and input rejection")
