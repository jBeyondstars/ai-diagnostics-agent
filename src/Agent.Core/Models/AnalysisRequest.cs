namespace Agent.Core.Models;

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
