using System.ComponentModel.DataAnnotations;

namespace Agent.Core.Configuration;

/// <summary>
/// Root configuration for the Diagnostics Agent.
/// </summary>
public sealed class AgentConfiguration
{
    public const string SectionName = "Agent";

    public LlmConfiguration Llm { get; set; } = new();
    public AppInsightsConfiguration AppInsights { get; set; } = new();
    public GitHubConfiguration GitHub { get; set; } = new();
    public ScheduleConfiguration Schedule { get; set; } = new();
}

/// <summary>
/// LLM provider configuration supporting Anthropic, OpenAI, and Azure OpenAI.
/// </summary>
public sealed class LlmConfiguration
{
    /// <summary>
    /// Provider to use: "Anthropic", "OpenAI", or "AzureOpenAI"
    /// </summary>
    [Required]
    public string Provider { get; set; } = "Anthropic";

    public AnthropicConfiguration Anthropic { get; set; } = new();
    public OpenAIConfiguration OpenAI { get; set; } = new();
    public AzureOpenAIConfiguration AzureOpenAI { get; set; } = new();
}

/// <summary>
/// Anthropic Claude configuration.
/// </summary>
public sealed class AnthropicConfiguration
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model to use.
    /// </summary>
    public string Model { get; set; } = "claude-opus-4-5-20251101";
}

public sealed class OpenAIConfiguration
{
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model to use.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    public string? Organization { get; set; }
}

public sealed class AzureOpenAIConfiguration
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
}

/// <summary>
/// Application Insights configuration.
/// </summary>
public sealed class AppInsightsConfiguration
{
    [Required]
    public string WorkspaceId { get; set; } = string.Empty;

    public bool UseManagedIdentity { get; set; } = true;
    public string? ConnectionString { get; set; }
}

/// <summary>
/// GitHub configuration for reading code and creating PRs.
/// </summary>
public sealed class GitHubConfiguration
{
    [Required]
    public string Token { get; set; } = string.Empty;

    [Required]
    public string Owner { get; set; } = string.Empty;

    [Required]
    public string Repo { get; set; } = string.Empty;

    public string DefaultBranch { get; set; } = "main";
    public bool EnableAutoMerge { get; set; }
    public string[] RequiredReviewers { get; set; } = [];
}

/// <summary>
/// Scheduling configuration for automated analysis runs.
/// </summary>
public sealed class ScheduleConfiguration
{
    [Range(1, 168)]
    public int IntervalHours { get; set; } = 6;

    [Range(1, 168)]
    public int HoursToAnalyze { get; set; } = 24;

    [Range(1, 1000)]
    public int MinExceptionOccurrences { get; set; } = 5;

    [Range(1, 100)]
    public int MaxExceptionsPerRun { get; set; } = 10;

    public bool AutoCreatePullRequests { get; set; }
    public bool AutoCreateIssues { get; set; } = true;
    public bool EnableWeekendRuns { get; set; } = true;

    public TimeOnly? QuietHoursStart { get; set; }
    public TimeOnly? QuietHoursEnd { get; set; }
}
