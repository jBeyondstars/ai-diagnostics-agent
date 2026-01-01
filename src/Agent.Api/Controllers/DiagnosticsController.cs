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
public class DiagnosticsController(
    DiagnosticsAgent agent,
    IOptions<AgentConfiguration> config,
    IDistributedCache cache,
    ILogger<DiagnosticsController> logger) : ControllerBase
{
    private readonly AgentConfiguration _config = config.Value;
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
        var workspaceId = _config.AppInsights.WorkspaceId;
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

            var tables = response.Value.Table.Rows
                .Select(r => r[0]?.ToString() ?? "")
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

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
            // Extract alert info from the common alert schema
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

            // Debounce: check if we've processed an alert recently
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

            // Set debounce marker
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
                    var report = await agent.AnalyzeAndFixAsync(AnalysisRequest.Latest, CancellationToken.None);
                    logger.LogInformation("Background analysis completed: {Fixes} fixes, PR={Url}",
                        report.TotalFixes, report.PullRequestUrl);
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
    /// Simple webhook endpoint for testing (no Azure Monitor payload required).
    /// </summary>
    [HttpPost("webhook/trigger")]
    [ProducesResponseType<WebhookResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<WebhookResponse>> TriggerAnalysis(
        [FromQuery] bool createPR = true,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Manual webhook trigger received, createPR={CreatePR}", createPR);

        var request = AnalysisRequest.Latest with { CreatePullRequest = createPR };
        var report = await agent.AnalyzeAndFixAsync(request, cancellationToken);

        return Ok(new WebhookResponse
        {
            Status = "completed",
            Message = $"Analysis completed: {report.TotalFixes} fixes proposed",
            PullRequestUrl = report.PullRequestUrl,
            TriggeredAt = DateTimeOffset.UtcNow.ToString("O")
        });
    }
}

public record WebhookResponse
{
    public required string Status { get; init; }
    public string? Message { get; init; }
    public string? AlertRule { get; init; }
    public string? LastProcessedAt { get; init; }
    public string? TriggeredAt { get; init; }
    public string? PullRequestUrl { get; init; }
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
