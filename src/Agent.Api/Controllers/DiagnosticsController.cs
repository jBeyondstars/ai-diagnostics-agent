using Agent.Core;
using Agent.Core.Configuration;
using Agent.Core.Models;
using Agent.Core.Services;
using Azure.Identity;
using Azure.Monitor.Query;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Agent.Api.Controllers;

/// <summary>
/// Main controller for AI Diagnostics Agent operations.
/// All exceptions are automatically tracked by Application Insights via OpenTelemetry.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public partial class DiagnosticsController(
    DiagnosticsAgent agent,
    IOptions<AgentConfiguration> config,
    IDistributedCache cache,
    DeduplicationService deduplicationService,
    ILogger<DiagnosticsController> logger) : ControllerBase
{
    private static readonly TimeSpan WebhookDebounce = TimeSpan.FromMinutes(5);
    /// <summary>
    /// Run a full exception analysis from Application Insights.
    /// </summary>
    [HttpPost("analyze")]
    [ProducesResponseType<DiagnosticReport>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<DiagnosticReport>> Analyze(
        [FromBody] AnalysisRequest? request,
        CancellationToken cancellationToken)
    {
        request ??= AnalysisRequest.Default;

        logger.LogInformation(
            "Starting analysis for {Hours}h with min {MinOccurrences} occurrences",
            request.HoursToAnalyze,
            request.MinOccurrences);

        var report = await agent.AnalyzeAndFixAsync(request, cancellationToken);

        logger.LogInformation(
            "Analysis completed: {ExceptionCount} exceptions found, {FixCount} fixes proposed",
            report.TotalExceptions,
            report.TotalFixes);

        return Ok(report);
    }

    /// <summary>
    /// Run a quick scan with minimal settings.
    /// </summary>
    [HttpPost("quick-scan")]
    [ProducesResponseType<DiagnosticReport>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<DiagnosticReport>> QuickScan(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting quick scan");

        var report = await agent.AnalyzeAndFixAsync(AnalysisRequest.QuickScan, cancellationToken);

        return Ok(report);
    }

    /// <summary>
    /// Analyze only the most recent exception and create a PR.
    /// </summary>
    [HttpPost("analyze-latest")]
    [ProducesResponseType<DiagnosticReport>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<DiagnosticReport>> AnalyzeLatest(CancellationToken cancellationToken)
    {
        logger.LogInformation("Analyzing latest exception only");

        var report = await agent.AnalyzeAndFixAsync(AnalysisRequest.Latest, cancellationToken);

        return Ok(report);
    }

    /// <summary>
    /// Get current agent status and configuration.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType<AgentStatus>(StatusCodes.Status200OK)]
    public ActionResult<AgentStatus> GetStatus()
    {
        var status = new AgentStatus
        {
            IsHealthy = true,
            Timestamp = DateTimeOffset.UtcNow,
            Runtime = Environment.Version.ToString(),
            Framework = "net10.0",
            SemanticKernelVersion = "1.45.0"
        };

        return Ok(status);
    }

    /// <summary>
    /// Clear the deduplication cache for a specific exception or all exceptions.
    /// Use this when a PR was closed without merging and you want to re-analyze.
    /// </summary>
    /// <param name="problemId">Optional: specific problemId to clear. If empty, clears all analyzed markers.</param>
    [HttpDelete("cache/dedup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ClearDeduplicationCache(
        [FromQuery] string? problemId = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(problemId))
        {
            await deduplicationService.ClearAsync(problemId, cancellationToken);
            logger.LogInformation("Cleared deduplication cache for: {ProblemId}", problemId);
            return Ok(new { message = $"Cleared cache for: {problemId}" });
        }

        // Clear all analyzed:* keys by pattern (requires listing known patterns)
        // For now, clear common exception types
        var commonTypes = new[]
        {
            "System.NullReferenceException",
            "System.InvalidOperationException",
            "System.DivideByZeroException",
            "System.ArgumentException",
            "System.FormatException",
            "System.IndexOutOfRangeException",
            "System.KeyNotFoundException"
        };

        foreach (var type in commonTypes)
        {
            await cache.RemoveAsync($"analyzed:{type}", cancellationToken);
        }

        logger.LogInformation("Cleared deduplication cache for common exception types");
        return Ok(new { message = "Cleared deduplication cache for common exception types" });
    }

    /// <summary>
    /// Test LLM connectivity.
    /// </summary>
    [HttpGet("test-llm")]
    [ProducesResponseType<LlmTestResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LlmTestResult>> TestLlm(CancellationToken cancellationToken)
    {
        logger.LogInformation("Testing LLM connectivity");

        var chunks = new List<string>();
        var request = AnalysisRequest.QuickScan with { MaxExceptionsToAnalyze = 1 };

        await foreach (var chunk in agent.AnalyzeStreamingAsync(request, cancellationToken).Take(5))
        {
            chunks.Add(chunk);
        }

        return Ok(new LlmTestResult
        {
            Success = true,
            Message = "LLM connection successful",
            SampleResponse = string.Join("", chunks)
        });
    }

    /// <summary>
    /// Test Application Insights connectivity and list available tables.
    /// </summary>
    [HttpGet("test-appinsights")]
    [ProducesResponseType<AppInsightsTestResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AppInsightsTestResult>> TestAppInsights(CancellationToken cancellationToken)
    {
        var workspaceId = config.Value.AppInsights.WorkspaceId;
        logger.LogInformation("Testing App Insights connection. WorkspaceId: {WorkspaceId}", workspaceId);

        if (string.IsNullOrEmpty(workspaceId))
        {
            return Ok(new AppInsightsTestResult
            {
                Success = false,
                WorkspaceId = "(not configured)",
                Message = "WorkspaceId is not configured",
                Error = "Set Agent:AppInsights:WorkspaceId in user secrets or configuration"
            });
        }

        try
        {
            var client = new LogsQueryClient(new DefaultAzureCredential());

            var response = await client.QueryWorkspaceAsync(
                workspaceId,
                "search * | distinct $table | take 20",
                new QueryTimeRange(TimeSpan.FromHours(24)),
                cancellationToken: cancellationToken);

            List<string> tables = [.. response.Value.Table.Rows
                .Select(r => r[0]?.ToString() ?? "")
                .Where(t => !string.IsNullOrEmpty(t))];

            return Ok(new AppInsightsTestResult
            {
                Success = true,
                WorkspaceId = workspaceId,
                Message = $"Connected successfully. Found {tables.Count} tables with data.",
                AvailableTables = tables
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to App Insights");
            return Ok(new AppInsightsTestResult
            {
                Success = false,
                WorkspaceId = workspaceId,
                Message = "Failed to connect to Application Insights",
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Webhook endpoint for Azure Monitor alerts.
    /// Configure this URL in your Azure Monitor Action Group.
    /// </summary>
    /// <remarks>
    /// This endpoint receives alerts from Azure Monitor when exceptions are detected.
    /// It includes debouncing to prevent multiple analyses within a short time window.
    /// </remarks>
    [HttpPost("webhook/alert")]
    [ProducesResponseType<WebhookResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WebhookResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<WebhookResponse>> OnAlertWebhook(
        [FromBody] JsonElement alertPayload,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Received Azure Monitor alert webhook");

        try
        {
            string alertId = "unknown";
            string alertRule = "unknown";
            string severity = "Sev3";

            if (alertPayload.TryGetProperty("data", out var data) &&
                data.TryGetProperty("essentials", out var essentials))
            {
                if (essentials.TryGetProperty("alertId", out var alertIdProp))
                    alertId = alertIdProp.GetString() ?? "unknown";

                if (essentials.TryGetProperty("alertRule", out var ruleProp))
                    alertRule = ruleProp.GetString() ?? "unknown";

                if (essentials.TryGetProperty("severity", out var sevProp))
                    severity = sevProp.GetString() ?? "Sev3";
            }

            logger.LogInformation("Alert received: Rule={Rule}, Severity={Severity}, AlertId={AlertId}",
                alertRule, severity, alertId);

            // check if we've processed an alert recently
            var debounceKey = $"webhook:debounce:{alertRule}";
            var lastProcessed = await cache.GetStringAsync(debounceKey, cancellationToken);

            if (lastProcessed != null)
            {
                logger.LogInformation("Debouncing alert {Rule} - last processed at {Time}", alertRule, lastProcessed);
                return StatusCode(429, new WebhookResponse
                {
                    Status = "debounced",
                    Message = $"Alert was recently processed. Next analysis allowed after debounce period.",
                    AlertRule = alertRule,
                    LastProcessedAt = lastProcessed
                });
            }

            await cache.SetStringAsync(
                debounceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = WebhookDebounce
                },
                cancellationToken);

            // Run analysis in the background to respond quickly to Azure
            _ = Task.Run(async () =>
            {
                try
                {
                    logger.LogInformation("Starting background analysis triggered by alert {Rule}", alertRule);
                    var request = new AnalysisRequest
                    {
                        HoursToAnalyze = 1,
                        CreatePullRequest = true,
                        MinOccurrences = 1,
                        MaxExceptionsToAnalyze = 10,
                        LatestOnly = false
                    };
                    var report = await agent.AnalyzeAndFixAsync(request, CancellationToken.None);
                    logger.LogInformation("Background analysis completed: {Fixes} fixes, {PRs} PRs created",
                        report.TotalFixes, report.TotalPullRequests);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background analysis failed for alert {Rule}", alertRule);
                }
            }, cancellationToken);

            return Ok(new WebhookResponse
            {
                Status = "accepted",
                Message = "Alert received. Analysis started in background.",
                AlertRule = alertRule,
                TriggeredAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing alert webhook");
            return Ok(new WebhookResponse
            {
                Status = "error",
                Message = ex.Message
            });
        }
    }

    /// <summary>
    /// GitHub webhook endpoint for PR events.
    /// Automatically clears the deduplication cache when a PR is closed without merging.
    /// Configure this URL in your GitHub repo: Settings > Webhooks > Add webhook
    /// Set Content-Type to application/json and select "Pull requests" events.
    /// </summary>
    [HttpPost("webhook/github")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> OnGitHubWebhook(
        [FromBody] JsonElement payload,
        [FromHeader(Name = "X-GitHub-Event")] string? eventType,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Received GitHub webhook: {EventType}", eventType);

        if (eventType != "pull_request")
        {
            return Ok(new { message = $"Ignored event type: {eventType}" });
        }

        try
        {
            if (!payload.TryGetProperty("action", out var actionProp))
            {
                return Ok(new { message = "No action in payload" });
            }

            var action = actionProp.GetString();

            if (action != "closed")
            {
                logger.LogDebug("Ignoring PR action: {Action}", action);
                return Ok(new { message = $"Ignored action: {action}" });
            }

            if (!payload.TryGetProperty("pull_request", out var prProp))
            {
                return Ok(new { message = "No pull_request in payload" });
            }

            var merged = prProp.TryGetProperty("merged", out var mergedProp) && mergedProp.GetBoolean();
            var prNumber = prProp.TryGetProperty("number", out var numProp) ? numProp.GetInt32() : 0;
            var prTitle = prProp.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : "";

            if (merged)
            {
                logger.LogInformation("PR #{Number} was merged - no cache clear needed", prNumber);
                return Ok(new { message = $"PR #{prNumber} merged - fix applied" });
            }

            logger.LogInformation("PR #{Number} closed without merge - clearing dedup cache", prNumber);

            var exceptionType = ExtractExceptionTypeFromTitle(prTitle ?? "");

            if (!string.IsNullOrEmpty(exceptionType))
            {
                // Clear all cache entries containing this exception type
                // Since we don't have the exact problemId, clear by pattern
                await deduplicationService.ClearByPatternAsync(exceptionType, cancellationToken);
                logger.LogInformation("Cleared cache for exception type: {Type}", exceptionType);

                return Ok(new {
                    message = $"PR #{prNumber} closed - cache cleared for {exceptionType}",
                    exceptionType
                });
            }

            return Ok(new { message = $"PR #{prNumber} closed - could not extract exception type from title" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing GitHub webhook");
            return Ok(new { error = ex.Message });
        }
    }

    private static string? ExtractExceptionTypeFromTitle(string title)
    {
        var match = MyRegex().Match(title);

        if (match.Success)
        {
            return "System." + match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Simple webhook endpoint for testing (no Azure Monitor payload required).
    /// </summary>
    /// <param name="createPR">Whether to create pull requests for fixes</param>
    /// <param name="hours">Hours to look back for exceptions (default: 1)</param>
    /// <param name="maxExceptions">Maximum number of exceptions to analyze (default: 10)</param>
    /// <param name="latestOnly">If true, only analyze the most recent exception (default: false)</param>
    [HttpPost("webhook/trigger")]
    [ProducesResponseType<WebhookResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<WebhookResponse>> TriggerAnalysis(
        [FromQuery] bool createPR = true,
        [FromQuery] int hours = 1,
        [FromQuery] int maxExceptions = 10,
        [FromQuery] bool latestOnly = false,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Manual webhook trigger: createPR={CreatePR}, hours={Hours}, maxExceptions={Max}, latestOnly={LatestOnly}",
            createPR, hours, maxExceptions, latestOnly);

        var request = new AnalysisRequest
        {
            HoursToAnalyze = hours,
            CreatePullRequest = createPR,
            MinOccurrences = 1,
            MaxExceptionsToAnalyze = maxExceptions,
            LatestOnly = latestOnly
        };

        var report = await agent.AnalyzeAndFixAsync(request, cancellationToken);

        return Ok(new WebhookResponse
        {
            Status = "completed",
            Message = $"Analysis completed: {report.TotalFixes} fixes proposed, {report.TotalPullRequests} PRs created",
            PullRequestUrl = report.PullRequestUrl,
            PullRequestUrls = report.PullRequestUrls,
            TriggeredAt = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Handle\s+(\w+Exception)", System.Text.RegularExpressions.RegexOptions.IgnoreCase, "fr-FR")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}

public record WebhookResponse
{
    public required string Status { get; init; }
    public string? Message { get; init; }
    public string? AlertRule { get; init; }
    public string? LastProcessedAt { get; init; }
    public string? TriggeredAt { get; init; }
    public string? PullRequestUrl { get; init; }
    public List<string>? PullRequestUrls { get; init; }
}

public record AgentStatus
{
    public bool IsHealthy { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public required string Runtime { get; init; }
    public required string Framework { get; init; }
    public required string SemanticKernelVersion { get; init; }
}

public record LlmTestResult
{
    public bool Success { get; init; }
    public required string Message { get; init; }
    public string? SampleResponse { get; init; }
}

public record AppInsightsTestResult
{
    public bool Success { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Message { get; init; }
    public string? Error { get; init; }
    public List<string>? AvailableTables { get; init; }
}
