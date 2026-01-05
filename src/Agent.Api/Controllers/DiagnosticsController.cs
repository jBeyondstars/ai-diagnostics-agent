using Agent.Api.Services.Background;
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
using System.Text.RegularExpressions;

namespace Agent.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public partial class DiagnosticsController(
    DiagnosticsAgent agent,
    AlertChannel alertChannel,
    IOptions<AgentConfiguration> config,
    IDistributedCache cache,
    DeduplicationService deduplicationService,
    ILogger<DiagnosticsController> logger) : ControllerBase
{
    private static readonly TimeSpan WebhookDebounce = TimeSpan.FromMinutes(5);
    
    // Cache for common exception types to avoid reallocation
    private static readonly string[] CommonExceptionTypes = 
    [
        "System.NullReferenceException",
        "System.InvalidOperationException",
        "System.DivideByZeroException",
        "System.ArgumentException",
        "System.FormatException",
        "System.IndexOutOfRangeException",
        "System.KeyNotFoundException"
    ];

    [HttpPost("analyze")]
    [ProducesResponseType<DiagnosticReport>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiagnosticReport>> Analyze(
        [FromBody] AnalysisRequest? request,
        CancellationToken ct)
    {
        request ??= AnalysisRequest.Default;
        logger.LogInformation("Starting analysis. Hours: {Hours}, MinOccurrences: {Min}", 
            request.HoursToAnalyze, request.MinOccurrences);

        var report = await agent.AnalyzeAndFixAsync(request, ct);
        return Ok(report);
    }

    [HttpPost("quick-scan")]
    [ProducesResponseType<DiagnosticReport>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiagnosticReport>> QuickScan(CancellationToken ct)
    {
        return Ok(await agent.AnalyzeAndFixAsync(AnalysisRequest.QuickScan, ct));
    }

    [HttpPost("analyze-latest")]
    [ProducesResponseType<DiagnosticReport>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiagnosticReport>> AnalyzeLatest(CancellationToken ct)
    {
        return Ok(await agent.AnalyzeAndFixAsync(AnalysisRequest.Latest, ct));
    }

    [HttpGet("status")]
    public ActionResult<AgentStatus> GetStatus() => Ok(new AgentStatus
    {
        IsHealthy = true,
        Timestamp = DateTimeOffset.UtcNow,
        Runtime = Environment.Version.ToString(),
        Framework = "net10.0",
        SemanticKernelVersion = "1.45.0"
    });

    [HttpDelete("cache/dedup")]
    public async Task<ActionResult> ClearDeduplicationCache(
        [FromQuery] string? problemId = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(problemId))
        {
            await deduplicationService.ClearAsync(problemId, ct);
            return Ok(new { message = $"Cleared cache for: {problemId}" });
        }

        foreach (var type in CommonExceptionTypes)
        {
            await cache.RemoveAsync($"analyzed:{type}", ct);
        }

        return Ok(new { message = "Cleared deduplication cache for common exception types" });
    }

    [HttpPost("webhook/alert")]
    [ProducesResponseType<WebhookResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<WebhookResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<WebhookResponse>> OnAlertWebhook(
        [FromBody] JsonElement alertPayload,
        CancellationToken ct)
    {
        var (alertRule, alertId, severity) = ParseAlertPayload(alertPayload);
        
        if (await IsDebounced(alertRule, ct))
        {
            return StatusCode(429, new WebhookResponse
            {
                Status = "debounced",
                AlertRule = alertRule,
                Message = "Alert processed recently."
            });
        }

        await alertChannel.WriteAsync(new AlertContext(alertRule, IsHybrid: false), ct);

        return Accepted(new WebhookResponse
        {
            Status = "accepted",
            AlertRule = alertRule,
            Message = "Alert queued for processing."
        });
    }

    [HttpPost("webhook/alert-hybrid")]
    [ProducesResponseType<WebhookResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<WebhookResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<WebhookResponse>> OnAlertWebhookHybrid(
        [FromBody] JsonElement alertPayload,
        CancellationToken ct)
    {
        var (alertRule, alertId, severity) = ParseAlertPayload(alertPayload);

        if (await IsDebounced($"hybrid:{alertRule}", ct))
        {
             return StatusCode(429, new WebhookResponse
            {
                Status = "debounced",
                AlertRule = alertRule,
                Message = "Hybrid alert processed recently."
            });
        }

        await alertChannel.WriteAsync(new AlertContext(alertRule, IsHybrid: true), ct);

        return Accepted(new WebhookResponse
        {
            Status = "accepted",
            AlertRule = alertRule,
            Message = "Hybrid alert queued for processing."
        });
    }

    [HttpPost("webhook/github")]
    public async Task<ActionResult> OnGitHubWebhook(
        [FromBody] JsonElement payload,
        [FromHeader(Name = "X-GitHub-Event")] string? eventType,
        CancellationToken ct)
    {
        if (eventType != "pull_request") return Ok(new { message = "Ignored event type" });

        if (!payload.TryGetProperty("action", out var actionProp) || actionProp.GetString() != "closed")
        {
            return Ok(new { message = "Ignored action" });
        }

        if (!payload.TryGetProperty("pull_request", out var prProp)) return Ok();

        if (prProp.TryGetProperty("merged", out var mergedProp) && mergedProp.GetBoolean())
        {
            return Ok(new { message = "PR merged" });
        }

        var prTitle = prProp.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : "";
        var exceptionType = ExtractExceptionTypeFromTitle(prTitle ?? "");

        if (!string.IsNullOrEmpty(exceptionType))
        {
            await deduplicationService.ClearByPatternAsync(exceptionType, ct);
            logger.LogInformation("Cleared cache for exception type: {Type}", exceptionType);
        }

        return Ok(new { message = "PR closed without merge, cache updated" });
    }

    [HttpPost("analyze-hybrid")]
    public async Task<ActionResult<HybridAnalysisResponse>> AnalyzeHybrid(
        [FromBody] HybridAnalysisRequest? request,
        CancellationToken ct)
    {
        request ??= new HybridAnalysisRequest();
        
        // Note: Logic kept inline as requested for specific hybrid flow
        // In a full refactor, this logic would move to DiagnosticsAgent entirely
        
        var appInsightsPlugin = new Agent.Plugins.AppInsightsPlugin(
            config.Value.AppInsights.WorkspaceId,
            logger as ILogger<Agent.Plugins.AppInsightsPlugin>);

        var exceptionsJson = await appInsightsPlugin.GetExceptionsAsync(
            request.HoursToAnalyze,
            request.MinOccurrences,
            request.MaxExceptions);

        List<ExceptionInfo> exceptions;
        try 
        {
            using var doc = JsonDocument.Parse(exceptionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Ok(new HybridAnalysisResponse { Status = "no_exceptions" });
            
            exceptions = doc.RootElement.EnumerateArray()
                .Select(ParseExceptionInfo)
                .ToList();
        }
        catch (JsonException)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid AppInsights Response" });
        }

        if (exceptions.Count == 0) return Ok(new HybridAnalysisResponse { Status = "no_exceptions" });

        var results = new List<HybridExceptionResult>();
        var prs = new List<string>();

        foreach (var ex in exceptions.Take(request.MaxExceptions))
        {
            try
            {
                var result = await agent.AnalyzeHybridAsync(ex, request.CreatePullRequest, ct);
                results.Add(new HybridExceptionResult 
                { 
                    ExceptionType = ex.ExceptionType, 
                    Action = result.Action, 
                    RootCause = result.RootCause,
                    PrUrl = result.PrUrl,
                    FixProposed = result.Fix != null 
                });
                
                if (!string.IsNullOrEmpty(result.PrUrl)) prs.Add(result.PrUrl);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Hybrid analysis failed for {Type}", ex.ExceptionType);
                results.Add(new HybridExceptionResult 
                { 
                    ExceptionType = ex.ExceptionType, 
                    Action = "error", 
                    RootCause = e.Message,
                    FixProposed = false 
                });
            }
        }

        return Ok(new HybridAnalysisResponse
        {
            Status = "completed",
            ExceptionsAnalyzed = results.Count,
            FixesProposed = results.Count(r => r.FixProposed),
            PullRequestsCreated = prs.Count,
            PullRequestUrls = prs,
            Results = results
        });
    }

    [HttpGet("test-llm")]
    public async Task<ActionResult<LlmTestResult>> TestLlm(CancellationToken ct)
    {
        var chunks = new List<string>();
        await foreach (var chunk in agent.AnalyzeStreamingAsync(AnalysisRequest.QuickScan with { MaxExceptionsToAnalyze = 1 }, ct).Take(5))
        {
            chunks.Add(chunk);
        }
        return Ok(new LlmTestResult { Success = true, Message = "LLM Connected", SampleResponse = string.Join("", chunks) });
    }

    [HttpGet("test-appinsights")]
    public async Task<ActionResult<AppInsightsTestResult>> TestAppInsights(CancellationToken ct)
    {
        var workspaceId = config.Value.AppInsights.WorkspaceId;
        if (string.IsNullOrEmpty(workspaceId))
            return Ok(new AppInsightsTestResult { Success = false, WorkspaceId = "N/A", Message = "Missing Configuration" });

        try
        {
            var client = new LogsQueryClient(new DefaultAzureCredential());
            var response = await client.QueryWorkspaceAsync(
                workspaceId,
                "search * | distinct $table | take 20",
                new QueryTimeRange(TimeSpan.FromHours(24)),
                cancellationToken: ct);

            var tables = response.Value.Table.Rows
                .Select(r => r[0]?.ToString() ?? "")
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            return Ok(new AppInsightsTestResult { Success = true, WorkspaceId = workspaceId, Message = "Connected", AvailableTables = tables });
        }
        catch (Exception ex)
        {
            return Ok(new AppInsightsTestResult { Success = false, WorkspaceId = workspaceId, Message = "Connection Failed", Error = ex.Message });
        }
    }

    // --- Helpers ---

    private async Task<bool> IsDebounced(string key, CancellationToken ct)
    {
        var dbKey = $"webhook:debounce:{key}";
        if (await cache.GetStringAsync(dbKey, ct) != null) return true;

        await cache.SetStringAsync(dbKey, DateTimeOffset.UtcNow.ToString("O"), 
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = WebhookDebounce }, ct);
        return false;
    }

    private static (string Rule, string Id, string Severity) ParseAlertPayload(JsonElement payload)
    {
        string rule = "unknown", id = "unknown", sev = "Sev3";
        if (payload.TryGetProperty("data", out var data) && data.TryGetProperty("essentials", out var essentials))
        {
            rule = essentials.TryGetProperty("alertRule", out var r) ? r.GetString() ?? rule : rule;
            id = essentials.TryGetProperty("alertId", out var i) ? i.GetString() ?? id : id;
            sev = essentials.TryGetProperty("severity", out var s) ? s.GetString() ?? sev : sev;
        }
        return (rule, id, sev);
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
        LineNumber = item.TryGetProperty("LineNumber", out var ln) && ln.ValueKind == JsonValueKind.Number ? ln.GetInt32() : null,
        ItemId = item.TryGetProperty("ItemId", out var i) ? i.GetString() : null
    };

    private static string? ExtractExceptionTypeFromTitle(string title)
    {
        var match = ExceptionRegex().Match(title);
        return match.Success ? "System." + match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"Handle\s+(\w+Exception)", RegexOptions.IgnoreCase, "fr-FR")]
    private static partial Regex ExceptionRegex();
}