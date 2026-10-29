using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Twenty.Core.Research;

namespace Twenty.Research;

/// <summary>The scientific service composition and HTTP admission policy belong to the research identity, not the broker host.</summary>
public static class ResearchModule
{
    public static ResearchStore Register(IServiceCollection services, string root)
    {
        var store = new ResearchStore(root);
        foreach (var (file, name) in new[] { ("demo", "Synthetic monthly fixture"), ("stress", "Synthetic growth and crash stress") })
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Demo");
            store.Import(File.ReadAllBytes(Path.Combine(directory, file + ".csv")), File.ReadAllBytes(Path.Combine(directory, file + ".metadata.json")), name);
        }
        services.AddSingleton(store); services.AddSingleton(new DiagnosticStore(store)); services.AddHostedService<ExperimentWorker>(); return store;
    }
    public static void Map(IEndpointRouteBuilder endpoints, ResearchStore store, DiagnosticStore diagnostics, Func<object> brokerCapabilities)
    {
        var api = endpoints.MapGroup("/api/research");
        api.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            if (request.Method is "POST" or "PUT" or "DELETE")
            {
                var origin = request.Headers.Origin.ToString();
                if (request.Headers["X-Twenty-Request"] != "1" || request.Headers["Sec-Fetch-Site"] == "cross-site" ||
                    (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Host != request.Host.Host)))
                    return Results.Json(new { error = "same-origin-required" }, statusCode: 403);
            }
            return await next(context);
        });
        static string Commit() => Environment.GetEnvironmentVariable("TWENTY_COMMIT") ?? "local";
        api.MapGet("/datasets", () => store.Datasets());
        api.MapPost("/datasets/import", (InboxImportRequest request) => Results.Ok(store.ImportInbox(request)));
        api.MapGet("/runs", () => store.ListRuns().Take(100));
        api.MapGet("/trials", () =>
        {
            var modelTrials = store.ListRuns().Count;
            var universeTrials = store.ListUniverseRuns().Count;
            var rawTrials = store.ListRawTrials().Count;
            return new { total = modelTrials + universeTrials + rawTrials, modelTrials, universeTrials, rawTrials, displayedLimit = 100 };
        });
        api.MapGet("/raw-paper-accounts", () => Results.Json(new { authoredOnly=true, corporateActions=true, pureSplitsAvailable=true,
            actionSupport=new { pureSplits=true, dividends=true, terminals=true, pendingClaims=true, pendingDividendClaims=true, pendingTerminalClaims=true },
            dividendMode=RawPaperContract.DividendMode, dividendScope="paid-before-monthly-marks",
            pendingDividendMode=RawPaperContract.DividendClaimsMode, pendingDividendScope="paid-by-final-monthly-mark",
            terminalMode=RawPaperContract.TerminalMode, terminalScope="settled-before-monthly-marks",
            pendingTerminalMode=RawPaperContract.TerminalClaimsMode, pendingTerminalScope="settled-by-final-monthly-mark",
            asynchronousFills=false, preparationAvailable=true, accounts=store.RawPaperAccounts() }, RawReplayJson.Options));
        api.MapPost("/raw-paper-accounts", (RawPaperCreateRequest request) => Results.Json(store.CreateRawPaperAccount(request),RawReplayJson.Options));
        api.MapGet("/raw-paper-accounts/{id}/operations", (string id) => Results.Json(store.RawPaperOperations(id),RawReplayJson.Options));
        api.MapPost("/raw-paper-accounts/{id}/advance", (string id,RawPaperAdvanceRequest request,CancellationToken ct) => Results.Json(store.AdvanceRawPaperAccount(id,request,ct),RawReplayJson.Options));
        api.MapPost("/raw-paper-accounts/{id}/prepare", (string id,RawPaperPrepareRequest request,CancellationToken ct) => Results.Json(store.PrepareRawPaperBatch(id,request,ct),RawReplayJson.Options));
        api.MapPost("/raw-paper-accounts/{id}/stop", (string id) => Results.Json(store.StopRawPaperAccount(id),RawReplayJson.Options));
        api.MapGet("/raw-paper-operations/{id}/receipt", (string id) => Results.Bytes(store.RawPaperReceiptBytes(id),"application/json"));
        api.MapGet("/raw-studies", () => Results.Json(new { executionAvailable = true, selectionAvailable = true, finalEvaluationAvailable = true, publicationRecoveryAvailable = true, planPreparationAvailable = true, studies = store.RawStudies() }, RawReplayJson.Options));
        api.MapPost("/raw-study-plans", (RawStudyPlanPreparationRequest request) => Results.Json(store.PrepareRawStudyPlan(request), RawReplayJson.Options));
        api.MapPost("/raw-studies", (RawStudyInboxRequest request) => Results.Json(store.RegisterRawStudy(new(request.RequestId, request.Name, PlanSha256: request.PlanSha256), Commit()), RawReplayJson.Options));
        api.MapGet("/raw-studies/{id}", (string id) => store.RawStudies().SingleOrDefault(s => s.Id == id) is { } study
            ? Results.Json(study, RawReplayJson.Options) : Results.NotFound());
        api.MapGet("/raw-studies/{id}/events", (string id) => store.RawStudyEvents(id));
        api.MapGet("/raw-studies/{id}/plan", (string id) => store.RawStudies().SingleOrDefault(s => s.Id == id) is { } study
            ? Results.Bytes(Convert.FromBase64String(study.PlanBase64), "application/json") : Results.NotFound());
        api.MapPost("/raw-studies/{id}/select", (string id, RawStudySelectionRequest request) => Results.Json(store.SelectRawStudy(id, request, Commit()), RawReplayJson.Options));
        api.MapGet("/raw-studies/{id}/selection", (string id) => Results.Bytes(store.RawStudySelectionBytes(id), "application/json"));
        api.MapPost("/raw-studies/{id}/evaluate-final", (string id, StudyEvaluationRequest request) => Results.Json(store.EvaluateRawStudy(id, request, Commit()), RawReplayJson.Options));
        api.MapPost("/raw-studies/{id}/recover-publication", (string id, StudyEvaluationRequest request, CancellationToken ct) => Results.Json(store.RecoverRawFinalPublication(id, request, ct), RawReplayJson.Options));
        api.MapPost("/raw-studies/{id}/start", (string id, RawStudyStartRequest request) => Results.Json(store.StartRawStudy(id, request), RawReplayJson.Options));
        api.MapGet("/raw-trials/{id}/report", (string id) => Results.Bytes(store.RawTrialReportBytes(id), "application/json"));
        api.MapPost("/raw-studies/{id}/cancel", (string id) => Results.Json(store.CancelRawStudy(id), RawReplayJson.Options));
        api.MapGet("/raw-trials", () => store.ListRawTrials());
        api.MapGet("/raw-trials/{id}/request", (string id) => Results.Bytes(store.RawTrialRequestBytes(id), "application/json"));
        api.MapPost("/runs", (RunRequest request) => Results.Accepted("/api/research/runs", store.Enqueue(request, Commit())));
        api.MapGet("/runs/{id}", (string id) => store.Find(id) is { } run ? Results.Ok(run) : Results.NotFound());
        api.MapGet("/runs/{id}/report", (string id) => store.Report(id) is { } report ? Results.Ok(report) : Results.NotFound());
        api.MapPost("/runs/{id}/cancel", (string id) => { store.Cancel(id); return Results.Accepted(); });
        api.MapGet("/studies", () => store.StudyHistory());
        api.MapGet("/studies/{id}", (string id) => store.Study(id) is { } study ? Results.Ok(study) : Results.NotFound());
        api.MapGet("/studies/{id}/plan", (string id) => store.StudyPlanJson(id) is { } json ? Results.Content(json, "application/json") : Results.NotFound());
        api.MapGet("/studies/{id}/events", (string id) => store.StudyEvents(id));
        api.MapPost("/studies", (StudyRequest request) => Results.Accepted("/api/research/studies", store.RegisterStudy(request, Commit())));
        api.MapPost("/studies/{id}/evaluate", (string id, StudyEvaluationRequest request) => Results.Accepted("/api/research/studies/" + id, store.EvaluateStudy(id, request, Commit())));
        api.MapPost("/studies/{id}/cancel", (string id) => store.CancelStudy(id));
        api.MapGet("/universe-datasets", () => store.UniverseDatasets());
        api.MapGet("/universe-studies", () =>
        {
            var runs = store.ListUniverseRuns();
            return store.UniverseStudies().Select(s => new { s.Id, s.Name, s.Status, s.CreatedAt, s.Plan.Purpose,
                s.Plan.Synthetic, s.PlanSha256, s.SelectedCandidateId, s.ErrorCode, s.FinalPassed,
                completedRuns = runs.Count(r => r.StudyId == s.Id && r.Status == "succeeded"),
                plannedRuns = s.DevelopmentRuns.Length * 2 + (s.FinalRequestedAt is null ? 0 : 2) });
        });
        api.MapGet("/universe-studies/{id}", (string id) => store.UniverseStudy(id) is { } study ? Results.Ok(study) : Results.NotFound());
        api.MapGet("/universe-studies/{id}/plan", (string id) => store.UniverseStudyPlanJson(id) is { } json ? Results.Content(json, "application/json") : Results.NotFound());
        api.MapGet("/universe-studies/{id}/events", (string id) => store.UniverseStudyEvents(id));
        api.MapPost("/universe-studies", (UniverseStudyRequest request) => Results.Accepted("/api/research/universe-studies", store.RegisterUniverseStudy(request, Commit())));
        api.MapPost("/universe-studies/{id}/evaluate", (string id, StudyEvaluationRequest request) => Results.Accepted("/api/research/universe-studies/" + id, store.EvaluateUniverseStudy(id, request, Commit())));
        api.MapPost("/universe-studies/{id}/cancel", (string id) => store.CancelUniverseStudy(id));
        api.MapPost("/universe-runs/import-dataset", (UniverseDatasetImportRequest request) =>
            Results.Ok(new { datasetSha256 = store.ImportUniverseDatasetInbox(request) }));
        api.MapGet("/universe-runs", () => store.ListUniverseRuns().Take(100));
        api.MapPost("/universe-runs", (UniverseRunRequest request) => Results.Ok(store.ExecuteUniverseRun(request, Commit())));
        api.MapGet("/universe-runs/{id}", (string id) => store.FindUniverseRun(id) is { } run ? Results.Ok(run) : Results.NotFound());
        api.MapGet("/universe-runs/{id}/request", (string id) => store.UniverseRequestBytes(id) is { } bytes ? Results.Bytes(bytes, "application/json") : Results.NotFound());
        api.MapGet("/universe-runs/{id}/report", (string id) => store.UniverseReport(id) is { } report ? Results.Ok(report) : Results.NotFound());
        api.MapGet("/diagnostics", () => diagnostics.History());
        api.MapGet("/diagnostics/{id}", (string id) => diagnostics.Find(id) is { } result ? Results.Ok(result) : Results.NotFound());
        api.MapPost("/diagnostics", async (DiagnosticRequest request, CancellationToken ct) => await diagnostics.Create(request, ct));
        api.MapGet("/capabilities", () => new { engine = Backtest.Version, nativeCpuTraining = true,
            hypotheses = StudyProtocol.Hypotheses, simultaneousJobs = 1, deadlineSeconds = 120, finalHoldoutEnabled = false,
            registeredStudyFinalEvaluation = true,
            registeredStudy = new { protocol = StudyProtocol.Version, codeSha256 = ResearchStore.StudyCodeSha256, identityScheme = ResearchIdentity.Scheme, finalWindowRequestRequired = true },
            researchIdentity = ResearchIdentity.Current,
            searchDiagnostics = new { method = SearchDiagnostics.Version, universeMethod = SearchDiagnostics.UniverseVersion, rawMethod = SearchDiagnostics.RawVersion,
                pairedCalendarResampling = true, completeTrialRegistrySnapshot = true, preregisteredPromotion = false },
            changingUniverse = new { panel = ChangingUniversePanel.BuilderVersion, backtest = UniverseBacktest.Version,
                registeredTrials = true, retrospectiveDiagnostics = true, registeredCandidateSelection = true,
                studyProtocol = UniverseStudyProtocol.Version, finalWindowRequestRequired = true, investmentNomination = false,
                units = "Local-currency adjusted returns in GBP notionals; no raw execution or FX conversion" }, broker = brokerCapabilities() });
    }
}
