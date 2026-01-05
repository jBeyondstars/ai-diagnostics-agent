namespace Agent.Core.Models;

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
