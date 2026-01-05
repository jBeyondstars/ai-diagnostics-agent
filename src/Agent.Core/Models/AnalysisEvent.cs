namespace Agent.Core.Models;

public sealed record FileChange
{
    public required string Path { get; init; }
    public required string Content { get; init; }
    public string? OriginalSha { get; init; }
}

public sealed record AnalysisEvent
{
    public required AnalysisEventType Type { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public object? Data { get; init; }
}

public enum AnalysisEventType
{
    Started,
    ExceptionsFound,
    AnalyzingException,
    ReadingSourceCode,
    ProposingFix,
    CreatingPullRequest,
    CreatingIssue,
    Completed,
    Error
}
