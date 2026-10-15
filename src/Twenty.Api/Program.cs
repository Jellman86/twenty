using Twenty.Core;
using Twenty.Core.Research;
using Twenty.Api.Research;
using Twenty.Api.Execution;

if (args.Contains("--health-check"))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try { return (await client.GetAsync("http://127.0.0.1:8080/health/ready")).IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { return 1; }
    catch (TaskCanceledException) { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
var root = builder.Configuration["TWENTY_DATA_ROOT"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "workspace");
var store = new ResearchStore(root);
foreach (var (file, name) in new[] { ("demo", "Synthetic monthly fixture"), ("stress", "Synthetic growth and crash stress") })
{
    var directory = Path.Combine(AppContext.BaseDirectory, "Demo");
    store.Import(File.ReadAllBytes(Path.Combine(directory, file + ".csv")), File.ReadAllBytes(Path.Combine(directory, file + ".metadata.json")), name);
}
if (args.Length == 2 && args[0] == "--import-panel")
{
    try
    {
        var path = Path.GetFullPath(args[1]);
        var dataset = store.Import(Twenty.Core.Data.DatasetArchive.ReadBounded(path),
            Twenty.Core.Data.DatasetArchive.ReadBounded(Path.ChangeExtension(path, ".metadata.json")), Path.GetFileNameWithoutExtension(path));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(dataset, ResearchStore.Json));
        store.Dispose(); return 0;
    }
    catch (Exception error) when (error is ResearchException or IOException or UnauthorizedAccessException)
    { Console.Error.WriteLine("Panel import rejected; source files were not registered."); store.Dispose(); return 1; }
}
var ledger = new PaperLedger(store);
builder.Services.AddSingleton(ledger);
var broker = BrokerConfiguration.Create(builder.Configuration);
builder.Services.AddSingleton(store);
builder.Services.AddHostedService<ExperimentWorker>();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16384);
var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(store.Dispose);
app.Use(async (context, next) =>
{
    try
    {
        if (context.Request.Method is "POST" or "PUT" or "DELETE" && context.Request.Path.StartsWithSegments("/api"))
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (context.Request.Headers["X-Twenty-Request"] != "1" || context.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
                (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Host != context.Request.Host.Host)))
            { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "same-origin-required" }); return; }
        }
        await next();
    }
    catch (ResearchException error)
    { context.Response.StatusCode = error.Code.EndsWith("not-found", StringComparison.Ordinal) ? 404 : 400; await context.Response.WriteAsJsonAsync(new { error = error.Code }); }
});
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", mode = "research" }));
app.MapGet("/api/meta", () => new { name = "Twenty", mode = "Research only", brokerConnected = false,
    dataset = "No historical dataset connected", commit = Environment.GetEnvironmentVariable("TWENTY_COMMIT") ?? "local",
    roadmap = "https://github.com/users/Jellman86/projects/1" });
app.MapGet("/api/research/datasets", () => store.Datasets());
app.MapGet("/api/research/runs", () => store.ListRuns().Take(100));
app.MapGet("/api/research/trials", () => new { total = store.ListRuns().Count, displayedLimit = 100 });
app.MapPost("/api/research/runs", (RunRequest request) => Results.Accepted("/api/research/runs", store.Enqueue(request, Environment.GetEnvironmentVariable("TWENTY_COMMIT") ?? "local")));
app.MapGet("/api/research/runs/{id}", (string id) => store.Find(id) is { } run ? Results.Ok(run) : Results.NotFound());
app.MapGet("/api/research/runs/{id}/report", (string id) => store.Report(id) is { } report ? Results.Ok(report) : Results.NotFound());
app.MapPost("/api/research/runs/{id}/cancel", (string id) => { store.Cancel(id); return Results.Accepted(); });
app.MapGet("/api/research/capabilities", () => new { engine = Backtest.Version, nativeCpuTraining = true,
    hypotheses = new[] { "ridge-relative-return", "momentum", "breakout", "upside-tail" },
    simultaneousJobs = 1, deadlineSeconds = 120, finalHoldoutEnabled = false,
    broker = new { mode = broker?.Environment.ToString().ToLowerInvariant() ?? "disconnected", credentialsConfigured = broker is not null,
        automaticPurchases = false, transportImplemented = true,
        gates = new[] { "Account-bound ISA identity", "Verified raw prices and quantity rules", "A validated current strategy", "Live execution coordinator and history/fill reconciliation" } } });
app.MapGet("/api/execution/paper", () => new { mode = "simulation", paused = ledger.Paused, accounts = ledger.Accounts() });
app.MapPost("/api/execution/paper", (RehearsalRequest request) => Results.Accepted("/api/execution/paper", ledger.Rehearse(request)));
app.MapPost("/api/execution/paper/{id}/reconcile", (string id, bool? partial) => ledger.Reconcile(id, partial ?? false));
app.MapPost("/api/execution/paper/{id}/cancel", (string id) => ledger.Reconcile(id, cancel: true));
app.MapPost("/api/execution/pause", (bool paused) => { ledger.Pause(paused); return Results.Ok(new { paused }); });
app.MapGet("/api/execution/broker/summary", async (CancellationToken ct) =>
{
    if (broker is null) return Results.BadRequest(new { error = "broker-credentials-missing" });
    try
    {
        var summary = await broker.Summary(ct);
        return Results.Ok(new { account = "…" + (summary.Id % 10000).ToString("D4"), summary.Currency, summary.Cash,
            environment = broker.Environment.ToString().ToLowerInvariant(), isaVerified = false });
    }
    catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
    { return Results.BadRequest(new { error = "broker-read-unavailable" }); }
});
app.MapGet("/api/roadmap", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "roadmap.json"), "application/json"));
app.MapPost("/api/projections", (ProjectionRequest request) =>
{
    try { return Results.Ok(Projection.Calculate(request)); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
});
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();
return 0;

public partial class Program { }
