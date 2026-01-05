using Agent.Core;
using Agent.Core.Configuration;
using Agent.Core.Models;
using Agent.Plugins;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Agent.Api.Services.Background;

public sealed class AlertProcessor(
    AlertChannel channel,
    IServiceProvider serviceProvider,
    IOptions<AgentConfiguration> config,
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

                if (alert.IsHybrid)
                {
                    await ProcessHybridAlertAsync(agent, alert.RuleName, stoppingToken);
                }
                else
                {
                    await ProcessStandardAlertAsync(agent, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process alert {Rule}", alert.RuleName);
            }
        }
    }

    private static async Task ProcessStandardAlertAsync(DiagnosticsAgent agent, CancellationToken ct)
    {
        var request = new AnalysisRequest
        {
            HoursToAnalyze = 1,
            CreatePullRequest = true,
            MinOccurrences = 1,
            MaxExceptionsToAnalyze = 10,
            LatestOnly = false
        };
        await agent.AnalyzeAndFixAsync(request, ct);
    }

    private async Task ProcessHybridAlertAsync(DiagnosticsAgent agent, string alertRule, CancellationToken ct)
    {
        logger.LogInformation("Starting HYBRID analysis for alert {Rule}", alertRule);

        // Phase 1: Pre-fetch exceptions directly from AppInsights (not via Claude)
        var appInsightsPlugin = new AppInsightsPlugin(
            config.Value.AppInsights.WorkspaceId,
            logger as ILogger<AppInsightsPlugin>);

        var exceptionsJson = await appInsightsPlugin.GetExceptionsAsync(
            hours: 1,
            minOccurrences: 1,
            maxResults: 10);

        // Phase 2: Parse exceptions
        List<ExceptionInfo> exceptions;
        try
        {
            using var doc = JsonDocument.Parse(exceptionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                logger.LogInformation("No exceptions found for hybrid analysis");
                return;
            }

            exceptions = doc.RootElement.EnumerateArray()
                .Select(ParseExceptionInfo)
                .ToList();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse AppInsights response");
            return;
        }

        if (exceptions.Count == 0)
        {
            logger.LogInformation("No exceptions to analyze");
            return;
        }

        logger.LogInformation("Found {Count} exceptions for hybrid analysis", exceptions.Count);

        // Phase 3: Analyze each exception with hybrid approach (pre-fetched data + tools available)
        var prsCreated = 0;
        foreach (var exception in exceptions)
        {
            try
            {
                var result = await agent.AnalyzeHybridAsync(
                    exception,
                    createPr: true,
                    ct);

                if (!string.IsNullOrEmpty(result.PrUrl))
                {
                    prsCreated++;
                    logger.LogInformation("HYBRID: Created PR for {Type}: {Url}",
                        exception.ExceptionType, result.PrUrl);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "HYBRID: Error analyzing {Type}", exception.ExceptionType);
            }
        }

        logger.LogInformation("HYBRID analysis completed for alert {Rule}: {PRs} PRs created", alertRule, prsCreated);
    }

    private static ExceptionInfo ParseExceptionInfo(JsonElement item) => new()
    {
        ExceptionType = item.GetProperty("ExceptionType").GetString() ?? "Unknown",
        Message = item.TryGetProperty("Message", out var m) ? m.GetString() ?? "" : "",
        StackTrace = item.TryGetProperty("StackTrace", out var s) ? s.GetString() ?? "" : "",
        OperationName = item.TryGetProperty("OperationName", out var o) ? o.GetString() ?? "" : "",
        Timestamp = DateTimeOffset.UtcNow,
        OccurrenceCount = item.TryGetProperty("OccurrenceCount", out var oc) ? oc.GetInt32() : 0,
        ProblemId = item.TryGetProperty("ProblemId", out var p) ? p.GetString() : null,
        SourceFile = item.TryGetProperty("SourceFile", out var sf) ? sf.GetString() : null,
        LineNumber = item.TryGetProperty("LineNumber", out var ln) && ln.ValueKind == JsonValueKind.Number
            ? ln.GetInt32()
            : null,
        ItemId = item.TryGetProperty("ItemId", out var i) ? i.GetString() : null,
        ItemTimestamp = item.TryGetProperty("Timestamp", out var its) ? its.GetString() : null
    };
}
