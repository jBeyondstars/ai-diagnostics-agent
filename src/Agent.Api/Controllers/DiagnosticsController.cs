using Agent.Core;
using Agent.Core.Models;
using Microsoft.AspNetCore.Mvc;

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
    ILogger<DiagnosticsController> logger) : ControllerBase
{
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
