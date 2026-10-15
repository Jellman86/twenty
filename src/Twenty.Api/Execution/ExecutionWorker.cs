using Twenty.Core.Research;

namespace Twenty.Api.Execution;

public sealed class ExecutionWorker(ExecutionCoordinator coordinator, ILogger<ExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? lastCode = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var policy = coordinator.Policy();
                if (policy is { Enabled: true, Automatic: true })
                {
                    var result = await coordinator.Run(stoppingToken);
                    if (result.Code != lastCode) logger.LogInformation("Execution worker state: {Status} {Code}", result.Status, result.Code);
                    lastCode = result.Code;
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                var code = error is ResearchException re ? re.Code : "execution-worker-failed";
                if (code != lastCode) logger.LogWarning("Execution worker stopped this cycle: {Code}", code);
                lastCode = code;
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
