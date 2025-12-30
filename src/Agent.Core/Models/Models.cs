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

    public string Severity => OccurrenceCount switch
    {
        > 100 => "Critical",
        > 50 => "High",
        > 10 => "Medium",
        _ => "Low"
    };
}

/// <summary>
/// Represents a proposed code fix.
/// </summary>
public sealed record CodeFix
{
    public required string FilePath { get; init; }
    public required string OriginalCode { get; init; }
    public required string FixedCode { get; init; }
    public required string Explanation { get; init; }
    public required string Severity { get; init; }

    public string Confidence { get; init; } = "Medium";
    public string[] RelatedExceptions { get; init; } = [];
    public string[] SuggestedTests { get; init; } = [];
}

/// <summary>
/// Complete diagnostic report.
/// </summary>
public sealed record DiagnosticReport
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required TimeSpan AnalysisPeriod { get; init; }
    public required List<ExceptionInfo> Exceptions { get; init; }
    public required List<CodeFix> ProposedFixes { get; init; }
    public required string Summary { get; init; }

    public string? PullRequestUrl { get; init; }
    public string? IssueUrl { get; init; }
    public decimal? EstimatedCost { get; init; }

    public int TotalExceptions => Exceptions.Count;
    public int TotalFixes => ProposedFixes.Count;
    public int CriticalExceptions => Exceptions.Count(e => e.Severity is "Critical");
}

/// <summary>
/// Request to trigger an analysis.
/// </summary>
public sealed record AnalysisRequest
{
    public int HoursToAnalyze { get; init; } = 24;
    public bool CreatePullRequest { get; init; }
    public bool CreateIssues { get; init; }
    public int MinOccurrences { get; init; } = 5;
    public int MaxExceptionsToAnalyze { get; init; } = 10;

    /// <summary>
    /// If true, only analyze the most recent exception (ignores MaxExceptionsToAnalyze)
    /// </summary>
    public bool LatestOnly { get; init; }

    public static AnalysisRequest Default => new();

    public static AnalysisRequest QuickScan => new()
    {
        HoursToAnalyze = 6,
        MinOccurrences = 10,
        MaxExceptionsToAnalyze = 5
    };

    public static AnalysisRequest FullAnalysis => new()
    {
        HoursToAnalyze = 24,
        MinOccurrences = 5,
        MaxExceptionsToAnalyze = 20,
        CreateIssues = true
    };

    public static AnalysisRequest Latest => new()
    {
        HoursToAnalyze = 1,
        MinOccurrences = 1,
        MaxExceptionsToAnalyze = 1,
        LatestOnly = true,
        CreatePullRequest = true
    };
}

/// <summary>
/// Represents a file change for a PR.
/// </summary>
public sealed record FileChange
{
    public required string Path { get; init; }
    public required string Content { get; init; }
    public string? OriginalSha { get; init; }
}

/// <summary>
/// Streaming analysis event for real-time updates.
/// </summary>
public sealed record AnalysisEvent
{
    public required AnalysisEventType Type { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public object? Data { get; init; }
}

/// <summary>
/// Types of analysis events.
/// </summary>
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
