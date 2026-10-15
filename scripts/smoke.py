"""Exercise the running image: HTTP contracts, durable training and isolated simulated orders."""
import hashlib
import json
import sys
import time
import urllib.error
import urllib.request
import uuid

base = sys.argv[1].rstrip("/")


def get(path):
    return urllib.request.urlopen(base + path, timeout=15)


def post(path, payload=None, expected=200, headers=None):
    request = urllib.request.Request(
        base + path, json.dumps(payload or {}).encode(),
        headers or {"Content-Type": "application/json", "X-Twenty-Request": "1"},
    )
    try:
        response = urllib.request.urlopen(request, timeout=15)
        assert response.status == expected, (path, response.status)
        return json.load(response)
    except urllib.error.HTTPError as error:
        assert error.code == expected, (path, error.code)
        return json.load(error)


assert json.load(get("/health/ready"))["mode"] == "research"
assert json.load(get("/api/meta"))["brokerConnected"] is False
roadmap = json.load(get("/api/roadmap"))
assert len(roadmap) >= 11
assert all(issue["url"].startswith("https://github.com/Jellman86/twenty/issues/") for issue in roadmap)
assert b'<div id="app">' in get("/").read()
result = post("/api/projections", {"monthly": 20, "years": 30, "annualReturn": 0.07, "target": 1000000})
assert result["points"][-1]["contributions"] == 7200
assert 23000 < result["points"][-1]["balance"] < 24000
assert 0.25 < result["requiredAnnualReturn"] < 0.27
post("/api/projections", {"monthly": -1, "years": 30, "annualReturn": 0.07}, 400)
post("/api/research/runs", {}, 403, {"Content-Type": "application/json"})
post("/api/research/runs", {}, 403, {"Content-Type": "application/json", "X-Twenty-Request": "1", "Origin": "https://untrusted.example"})
post("/api/research/runs", {"requestId": str(uuid.uuid4()), "datasetId": None, "config": {"allocationPolicy": "contribution-only"}, "costs": {}}, 404)
datasets = json.load(get("/api/research/datasets"))
assert len(datasets) >= 2
payload = {"requestId": str(uuid.uuid4()), "datasetId": next(d["id"] for d in datasets if "growth" in d["name"]), "config": {"allocationPolicy": "contribution-only"}, "costs": {}}
run = post("/api/research/runs", payload, 202)
assert post("/api/research/runs", payload, 202)["id"] == run["id"]
conflict = dict(payload, config={"topK": 2})
assert post("/api/research/runs", conflict, 400)["error"] == "request-id-conflict"
for _ in range(120):
    state = json.load(get(f'/api/research/runs/{run["id"]}'))
    if state["status"] not in ("queued", "running"):
        break
    time.sleep(0.5)
assert state["status"] == "succeeded", state
assert json.load(get("/api/research/trials"))["total"] >= 1
assert state["engineSha256"] and state["reportSha256"] and state["workerCommit"]
report = json.load(get(f'/api/research/runs/{run["id"]}/report'))
assert report["synthetic"] is True
assert report["config"]["allocationPolicy"] == "contribution-only"
assert all(t["side"] == "buy" for e in report["equity"] for t in e["state"]["trades"])
assert len(report["summaries"]) == 5
assert all(f["lastLabelEnd"] < f["decisionDate"] for f in report["folds"])
assert report["evaluationEnd"] <= report["reservedPeriodStart"]
paper_request = {"requestId": str(uuid.uuid4()), "runId": run["id"], "month": "2026-10"}
account = post("/api/execution/paper", paper_request, 202)
assert post("/api/execution/paper", paper_request, 202)["id"] == account["id"]
assert account["contributionsGbp"] == 20 and account["orders"]
assert sum(o["progress"]["remainingReserve"] for o in account["orders"]) <= 20
partial = post(f'/api/execution/paper/{account["id"]}/reconcile?partial=true')
assert all(o["progress"]["status"] == "partially_filled" for o in partial["orders"])
filled = post(f'/api/execution/paper/{account["id"]}/reconcile')
assert filled == post(f'/api/execution/paper/{account["id"]}/reconcile')
assert all(o["progress"]["status"] == "filled" for o in filled["orders"])
assert abs(filled["cashGbp"] + sum(o["progress"]["debitGbp"] for o in filled["orders"]) - 20) < 1e-8
capabilities = json.load(get("/api/research/capabilities"))
assert capabilities["broker"]["automaticPurchases"] is False
assert capabilities["finalHoldoutEnabled"] is False
runtime = json.load(get("/api/execution/broker"))
assert runtime["configured"] is False and runtime["enabled"] is False and runtime["credentialsConfigured"] is False
assert runtime["accounts"] == []
assert post("/api/execution/broker/cycle")["code"] == "execution-private-configuration-required"
assert post("/api/execution/broker/capture", expected=400)["error"] == "broker-credentials-missing"
assert post("/api/execution/signals", {"runId": run["id"], "datasetId": payload["datasetId"]}, 400)["error"] == "signal-current-panel-required"
challenger = post("/api/research/runs", dict(payload, requestId=str(uuid.uuid4()), config={"allocationPolicy": "contribution-only", "hypothesis": "momentum", "topK": 1}), 202)
for _ in range(120):
    challenger = json.load(get(f'/api/research/runs/{challenger["id"]}'))
    if challenger["status"] not in ("queued", "running"):
        break
    time.sleep(0.5)
assert challenger["status"] == "succeeded", challenger
analysis_request = {"requestId": str(uuid.uuid4()), "anchorRunId": run["id"]}
analysis = post("/api/research/diagnostics", analysis_request)
assert analysis["status"] == "succeeded" and analysis["synthetic"] is True
assert analysis["diagnosticEngineSha256"] and analysis["registrySnapshotAt"]
assert {run["id"], challenger["id"]} <= {t["runId"] for t in analysis["trials"] if t["included"]}
assert analysis["report"]["months"] >= 24
assert analysis["report"]["comparisons"] == analysis["report"]["candidates"] * 4
assert [s["expectedBlockMonths"] for s in analysis["report"]["sensitivities"]] == [3, 6, 12]
assert analysis == post("/api/research/diagnostics", analysis_request)
assert analysis == json.load(get(f'/api/research/diagnostics/{analysis["id"]}'))
assert post("/api/research/diagnostics", dict(analysis_request, anchorRunId="0" * 32), 400)["error"] == "request-id-conflict"
assert json.load(get("/api/research/diagnostics"))["total"] >= 1
capabilities = json.load(get("/api/research/capabilities"))
assert capabilities["registeredStudyFinalEvaluation"] is True
study_code = capabilities["registeredStudy"]["codeSha256"]
studies = json.load(get("/api/research/studies"))
existing = next((s for s in studies if s["synthetic"] and s["codeSha256"] == study_code and s["datasetId"] == payload["datasetId"] and s["status"] == "engineering-complete"), None)
if existing:
    study = json.load(get(f'/api/research/studies/{existing["id"]}'))
else:
    study_request = {"requestId": str(uuid.uuid4()), "datasetId": payload["datasetId"], "name": "Synthetic HTTP study acceptance", "purpose": "engineering", "monthlyGbp": 20, "costs": {}}
    study = post("/api/research/studies", study_request, 202)
    assert post("/api/research/studies", study_request, 202)["id"] == study["id"]
    assert len(study["developmentRuns"]) == len(study["plan"]["candidates"])
    for _ in range(360):
        study = json.load(get(f'/api/research/studies/{study["id"]}'))
        if study["status"] != "development":
            break
        time.sleep(0.5)
    assert study["status"] == "candidate-frozen", study.get("errorCode")
    assert len(study["scores"]) == len(study["plan"]["candidates"])
    final_request = {"requestId": str(uuid.uuid4()), "planSha256": study["planSha256"]}
    planned = post(f'/api/research/studies/{study["id"]}/evaluate', final_request, 202)
    assert planned["finalRequestedAt"] and planned["finalBaseRunId"] and planned["finalStressRunId"]
    assert post(f'/api/research/studies/{study["id"]}/evaluate', final_request, 202)["finalBaseRunId"] == planned["finalBaseRunId"]
    for _ in range(120):
        study = json.load(get(f'/api/research/studies/{study["id"]}'))
        if study["status"] != "final-evaluation":
            break
        time.sleep(0.5)
assert study["status"] == "engineering-complete", study.get("errorCode")
assert study["plan"]["synthetic"] and study["plan"]["purpose"] == "engineering"
assert study["plan"]["developmentEnd"] < study["plan"]["reservedStart"]
assert hashlib.sha256(get(f'/api/research/studies/{study["id"]}/plan').read()).hexdigest() == study["planSha256"]
assert study["selectionDiagnostics"]["candidates"] == 2 * len(study["plan"]["candidates"])
assert study["finalBaseReportSha256"] and study["finalStressReportSha256"]
final = json.load(get(f'/api/research/runs/{study["finalBaseRunId"]}'))
assert final["studyCandidateId"] == study["selectedCandidateId"]
assert final["startedAt"] >= study["finalRequestedAt"]
report = json.load(get(f'/api/research/runs/{final["id"]}/report'))
assert report["scope"] == "reserved_holdout"
assert all(f["lastLabelEnd"] < f["decisionDate"] for f in report["folds"])
assert post(f'/api/research/studies/{study["id"]}/evaluate', {"requestId": study["finalRequestId"], "planSha256": study["planSha256"]}, 202) == study
assert post(f'/api/research/studies/{study["id"]}/evaluate', {"requestId": str(uuid.uuid4()), "planSha256": study["planSha256"]}, 400)["error"] == "study-holdout-spent"
events = json.load(get(f'/api/research/studies/{study["id"]}/events'))
assert [e["kind"] for e in events] == ["registered-before-work", "development-selection-frozen", "holdout-spent-before-work", "final-decision-no-substitute"]
print("HTTP smoke passed: CPU trials, simulated purchases, candidate-family diagnostics and a finite one-time final study rehearsal")
