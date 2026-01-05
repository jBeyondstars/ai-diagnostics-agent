namespace Agent.Core.Models;

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
    public List<string> PullRequestUrls { get; init; } = [];
    public string? IssueUrl { get; init; }
    public decimal? EstimatedCost { get; init; }

    public int TotalExceptions => Exceptions.Count;
    public int TotalFixes => ProposedFixes.Count;
    public int TotalPullRequests => PullRequestUrls.Count;
    public int CriticalExceptions => Exceptions.Count(e => e.Severity is "Critical");
}
