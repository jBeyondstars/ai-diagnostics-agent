using Agent.Core;
using Agent.Core.Models;

namespace Agent.Api.Services.Background;

public sealed class AlertProcessor(
    AlertChannel channel,
    IServiceProvider serviceProvider,
    ILogger<AlertProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var alert in channel.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var agent = scope.ServiceProvider.GetRequiredService<DiagnosticsAgent>();

                logger.LogInformation("Processing alert: {Rule} (Hybrid: {IsHybrid})", alert.RuleName, alert.IsHybrid);

                var request = new AnalysisRequest
                {
                    HoursToAnalyze = 1,
                    CreatePullRequest = true,
                    MinOccurrences = 1,
                    MaxExceptionsToAnalyze = 10
                };

                if (alert.IsHybrid)
                {
                    await agent.AnalyzeBatchHybridAsync(request, stoppingToken);
                }
                else
                {
                    await agent.AnalyzeAndFixAsync(request, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process alert {Rule}", alert.RuleName);
            }
        }
    }
}