using Twenty.Core.Research;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Twenty.Research;

public sealed class ExperimentWorker(ResearchStore store, ILogger<ExperimentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (store.ProcessNextRawStudyRun(Environment.GetEnvironmentVariable("TWENTY_COMMIT") ?? "local", stoppingToken)) continue; }
            catch (Exception error) when (error is ResearchException or IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Raw study work unavailable: {Code}", error is ResearchException re ? re.Code : "research-register-unavailable");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); continue;
            }
            try { if (store.ProcessNextUniverseStudyRun(Environment.GetEnvironmentVariable("TWENTY_COMMIT") ?? "local", stoppingToken)) continue; }
            catch (Exception error) when (error is ResearchException or IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Universe study work unavailable: {Code}", error is ResearchException re ? re.Code : "research-register-unavailable");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); continue;
            }
            if (!store.TryBeginComputation()) { await Task.Delay(500, stoppingToken); continue; }
            var nextDelay = 0;
            try
            {
                RunEntry? run;
                try { run = store.Claim(); }
                catch (Exception error) when (error is ResearchException or IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
                {
                    // Per-run rejection is persisted by Claim. If the register itself is unavailable, retain the host and retry without selecting work.
                    logger.LogWarning("Research claim unavailable: {Code}", error is ResearchException re ? re.Code : "research-register-unavailable");
                    nextDelay = 5000; continue;
                }
                if (run is null) { nextDelay = 500; continue; }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); deadline.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    var panel = store.Panel(run.DatasetId);
                    var lastProgress = -1;
                    var report = Backtest.Run(panel, run.Config, run.Costs, deadline.Token, percent =>
                    {
                        var current = store.Find(run.Id)!;
                        if (current.CancelRequested) { deadline.Cancel(); deadline.Token.ThrowIfCancellationRequested(); }
                        if (percent / 10 != lastProgress / 10)
                        { lastProgress = percent; store.Progress(run.Id, percent); }
                    });
                    deadline.Token.ThrowIfCancellationRequested();
                    var final = store.Find(run.Id)!;
                    if (final.CancelRequested) throw new OperationCanceledException();
                    store.Complete(run.Id, report);
                }
                catch (OperationCanceledException)
                {
                    var current = store.Find(run.Id)!;
                    store.Save(current with { Status = stoppingToken.IsCancellationRequested ? "interrupted" : "cancelled", FinishedAt = DateTimeOffset.UtcNow,
                        ErrorCode = current.CancelRequested ? "user-cancelled" : "deadline-or-shutdown" }, "cancelled-or-interrupted");
                }
                catch (Exception error)
                {
                    var code = error is ResearchException re ? re.Code : "experiment-failed";
                    logger.LogWarning("Research run {RunId} failed: {Code}", run.Id, code);
                    store.Save(store.Find(run.Id)! with { Status = "failed", FinishedAt = DateTimeOffset.UtcNow, ErrorCode = code }, "failed");
                }
            }
            finally
            {
                store.EndComputation();
                if (nextDelay > 0) await Task.Delay(nextDelay, stoppingToken);
            }
        }
    }
}
