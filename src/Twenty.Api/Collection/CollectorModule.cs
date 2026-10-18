using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Twenty.Ingestion.Capture;

namespace Twenty.Api.Collection;

/// <summary>
/// Data collection runs in one of three places, all from the same image:
/// on Quark inside the app (role "local"), on a remote host headless with <c>--collector</c> (role "remote", shipping
/// to Quark), or not at all (the default). Quark accepts remote captures only with the private ingest token.
/// </summary>
public sealed class CollectorSettings
{
    public required string Root { get; init; }
    public CaptureRole? Role { get; init; }
    public string? Sources { get; init; }
    public Uri? Proxy { get; init; }
    public string? IngestToken { get; init; }
    public Uri? ShipTo { get; init; }
    public string Name { get; init; } = "local";
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);

    public static CollectorSettings From(IConfiguration configuration, string dataRoot, bool headless)
    {
        var role = (configuration["TWENTY_COLLECTOR_ROLE"] ?? (headless ? "remote" : "off")).Trim().ToLowerInvariant() switch
        {
            "off" or "" => (CaptureRole?)null,
            "local" => CaptureRole.Local,
            "remote" => CaptureRole.Remote,
            _ => throw new CaptureException("collector-role-invalid"),
        };
        if (headless && role != CaptureRole.Remote) throw new CaptureException("collector-headless-needs-remote-role");
        if (!headless && role == CaptureRole.Remote) throw new CaptureException("collector-remote-role-needs-headless");
        var token = Secret(configuration, "TWENTY_INGEST_TOKEN");
        if (token is not null && token.Length < 32) throw new CaptureException("ingest-token-too-short");
        Uri? Parse(string key, bool https)
        {
            var value = configuration[key];
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (https && uri.Scheme != Uri.UriSchemeHttps)) throw new CaptureException(key.ToLowerInvariant().Replace('_', '-') + "-invalid");
            return uri;
        }
        var settings = new CollectorSettings
        {
            Root = Path.Combine(dataRoot, "captures"), Role = role, Sources = configuration["TWENTY_COLLECTOR_SOURCES"], Proxy = Parse("TWENTY_COLLECTOR_PROXY", false),
            IngestToken = token, ShipTo = Parse("TWENTY_SHIP_URL", true),
            Name = configuration["TWENTY_COLLECTOR_NAME"] is { Length: > 0 } name ? name.Trim() : headless ? "remote" : "local",
        };
        if (role == CaptureRole.Remote && (settings.ShipTo is null || token is null)) throw new CaptureException("collector-remote-needs-ship-url-and-token");
        _ = CaptureSources.For(role ?? CaptureRole.Local, settings.Sources); // Unknown names fail at start-up, not at 3am.
        return settings;
    }

    private static string? Secret(IConfiguration configuration, string key)
    {
        if (configuration[key + "_FILE"] is { Length: > 0 } file) return File.ReadAllText(file).Trim();
        return configuration[key] is { Length: > 0 } value ? value.Trim() : null;
    }
}

public sealed class CollectorWorker(CollectorSettings settings, CaptureStore store, ILogger<CollectorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var role = settings.Role!.Value;
        var sources = CaptureSources.For(role, settings.Sources);
        using var http = CaptureCollector.CreateHttpClient(settings.Proxy);
        var collector = new CaptureCollector(http, store, role, new CaptureOptions { Collector = settings.Name });
        CaptureShipper? shipper = role == CaptureRole.Remote ? new CaptureShipper(http, store, settings.ShipTo!, settings.IngestToken!) : null;
        logger.LogInformation("Collector {Name} running as {Role} for {Sources}", settings.Name, role, string.Join(",", sources.Select(s => s.Id)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var outcome in await collector.RunDue(sources, stoppingToken))
                    if (outcome.Result != "not-due") logger.LogInformation("Capture {Source}: {Result} {Code}", outcome.SourceId, outcome.Result, outcome.Code);
                if (shipper is not null)
                {
                    var (shipped, error) = await shipper.ShipAll(stoppingToken);
                    if (shipped > 0 || error is not null) logger.LogInformation("Shipped {Count} captures {Error}", shipped, error);
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Collector cycle stopped: {Code}", error is CaptureException c ? c.Code : error.GetType().Name);
            }
            await Task.Delay(settings.Interval, stoppingToken);
        }
    }
}

public static class CollectorModule
{
    public static CaptureStore? Add(IServiceCollection services, CollectorSettings settings, bool tiingoConfigured = false)
    {
        services.AddSingleton(settings);
        if (settings.Role is null && settings.IngestToken is null && !tiingoConfigured) return null;
        var store = new CaptureStore(settings.Root);
        services.AddSingleton(store);
        if (settings.Role is not null) services.AddHostedService<CollectorWorker>();
        return store;
    }

    /// <summary>Runs a remote collector without the web host: collect what is due, ship it, sleep.</summary>
    public static async Task<int> RunHeadless(IConfiguration configuration, string dataRoot)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        var settings = CollectorSettings.From(configuration, dataRoot, headless: true);
        using var store = new CaptureStore(settings.Root);
        builder.Services.AddSingleton(settings); builder.Services.AddSingleton(store); builder.Services.AddHostedService<CollectorWorker>();
        await builder.Build().RunAsync();
        return 0;
    }

    public static void Map(IEndpointRouteBuilder endpoints, CollectorSettings settings, CaptureStore? store)
    {
        endpoints.MapGet("/api/ingest/status", () => new
        {
            role = settings.Role?.ToString().ToLowerInvariant() ?? "off",
            collector = settings.Name,
            receivingRemote = settings.IngestToken is not null,
            sources = CaptureSources.All.Select(source =>
            {
                var status = store?.Status(source.Id);
                var observations = store?.Observations(source.Id) ?? [];
                return new
                {
                    source.Id, source.Title, url = source.Url.AbsoluteUri, defaultHost = source.Host.ToString().ToLowerInvariant(), cadenceHours = source.Cadence.TotalHours,
                    collectedHere = settings.Role is { } role && CaptureSources.For(role, settings.Sources).Any(s => s.Id == source.Id),
                    status?.LastSuccessAt, status?.LastAttemptAt, status?.PausedUntil, status?.LastError, status?.ConsecutiveFailures,
                    lastSha256 = status?.LastSha256?[..12],
                    observations = observations.Count,
                    lastCollector = observations.LastOrDefault()?.Collector,
                };
            }),
        });

        if (store is null || settings.IngestToken is null) return;
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(settings.IngestToken));
        endpoints.MapPost("/api/ingest/captures", async (HttpContext context) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), expected))
                return Results.Json(new { error = "ingest-token-invalid" }, statusCode: 401);
            CaptureObservation observation;
            try
            {
                observation = JsonSerializer.Deserialize<CaptureObservation>(Convert.FromBase64String(context.Request.Headers[CaptureShipper.ObservationHeader].ToString()),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException();
            }
            catch (Exception error) when (error is FormatException or JsonException)
            { return Results.Json(new { error = "capture-observation-invalid" }, statusCode: 400); }
            if (observation.Bytes > CaptureStore.MaximumCaptureBytes) return Results.Json(new { error = "capture-too-large" }, statusCode: 413);
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = CaptureStore.MaximumCaptureBytes;
            var staged = store.StagingPath();
            try
            {
                await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await context.Request.Body.CopyToAsync(output, context.RequestAborted);
                var accepted = store.Receive(observation, staged, DateTimeOffset.UtcNow);
                return Results.Ok(new { accepted, observation.Sha256 });
            }
            catch (CaptureException error) { return Results.Json(new { error = error.Code }, statusCode: 400); }
            catch (BadHttpRequestException) { return Results.Json(new { error = "capture-too-large" }, statusCode: 413); }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }).AllowAnonymous();
    }
}
