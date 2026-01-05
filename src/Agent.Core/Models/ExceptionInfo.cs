namespace Agent.Core.Models;

/// <summary>
/// Represents an exception captured from Application Insights.
/// </summary>
public sealed record ExceptionInfo
{
    public required string ExceptionType { get; init; }
    public required string Message { get; init; }
    public required string StackTrace { get; init; }
    public required string OperationName { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required int OccurrenceCount { get; init; }

    public int AffectedUsersCount { get; init; }
    public string? ProblemId { get; init; }
    public Dictionary<string, string> CustomDimensions { get; init; } = [];
    public string? SourceFile { get; init; }
    public int? LineNumber { get; init; }
    public string? ItemId { get; init; }
    public string? ItemTimestamp { get; init; }

    public string Severity => OccurrenceCount switch
    {
        > 100 => "Critical",
        > 50 => "High",
        > 10 => "Medium",
        _ => "Low"
    };
}
