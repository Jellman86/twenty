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
            return new { total = modelTrials + universeTrials, modelTrials, universeTrials, displayedLimit = 100 };
        });
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
            searchDiagnostics = new { method = SearchDiagnostics.Version, pairedCalendarResampling = true, preregisteredPromotion = false },
            changingUniverse = new { panel = ChangingUniversePanel.BuilderVersion, backtest = UniverseBacktest.Version, registeredTrials = true }, broker = brokerCapabilities() });
    }
}
