namespace Agent.Api.Controllers;

public record WebhookResponse
{
    public required string Status { get; init; }
    public string? Message { get; init; }
    public string? AlertRule { get; init; }
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

public record HybridAnalysisRequest
{
    public int HoursToAnalyze { get; init; } = 24;
    public int MinOccurrences { get; init; } = 1;
    public int MaxExceptions { get; init; } = 5;
    public bool CreatePullRequest { get; init; } = false;
}

public record HybridAnalysisResponse
{
    public required string Status { get; init; }
    public string? Message { get; init; }
    public int ExceptionsAnalyzed { get; init; }
    public int FixesProposed { get; init; }
    public int PullRequestsCreated { get; init; }
    public List<string>? PullRequestUrls { get; init; }
    public List<HybridExceptionResult>? Results { get; init; }
}

public record HybridExceptionResult
{
    public required string ExceptionType { get; init; }
    public required string Action { get; init; }
    public string? RootCause { get; init; }
    public string? PrUrl { get; init; }
    public bool FixProposed { get; init; }
}
