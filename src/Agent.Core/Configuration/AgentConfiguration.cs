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
    public ExceptionFilterConfiguration ExceptionFilter { get; set; } = new();
}

/// <summary>
/// Configuration for filtering non-actionable exceptions.
/// </summary>
public sealed class ExceptionFilterConfiguration
{
    /// <summary>
    /// Exception types to always ignore (infrastructure/transient errors).
    /// These exceptions typically don't have code fixes.
    /// </summary>
    public string[] ExcludedTypes { get; set; } =
    [
        // Network/HTTP errors
        "System.Net.Http.HttpRequestException",
        "System.Net.Sockets.SocketException",
        "System.Net.WebException",

        // Timeout errors
        "System.TimeoutException",
        "System.Threading.Tasks.TaskCanceledException",
        "System.OperationCanceledException",

        // Database connection errors
        "Microsoft.Data.SqlClient.SqlException",
        "Npgsql.NpgsqlException",
        "MySqlConnector.MySqlException",

        // Redis/Cache errors
        "StackExchange.Redis.RedisConnectionException",
        "StackExchange.Redis.RedisTimeoutException",

        // Azure SDK transient errors
        "Azure.RequestFailedException",
        "Azure.Identity.CredentialUnavailableException",

        // Message queue errors
        "RabbitMQ.Client.Exceptions.BrokerUnreachableException",
        "Azure.Messaging.ServiceBus.ServiceBusException"
    ];

    /// <summary>
    /// Partial matches to exclude (e.g., "Connection" matches "SqlConnectionException").
    /// </summary>
    public string[] ExcludedPatterns { get; set; } =
    [
        "ConnectionException",
        "ConnectionRefused",
        "HostNotFound",
        "Unreachable",
        "Timeout",
        "NetworkError"
    ];

    /// <summary>
    /// If true, ask Claude to evaluate if ambiguous exceptions are fixable.
    /// </summary>
    public bool EnableClaudeEvaluation { get; set; } = true;

    /// <summary>
    /// Exception types that typically indicate input validation errors (client sent bad data).
    /// These will be evaluated by Claude to determine if they're real bugs or expected validation.
    /// </summary>
    public string[] InputValidationExceptionTypes { get; set; } =
    [
        "System.FormatException",
        "System.ArgumentException",
        "System.ArgumentNullException",
        "System.ArgumentOutOfRangeException",
        "System.InvalidCastException"
    ];
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
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-5-20250929";
}

public sealed class OpenAIConfiguration
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Organization { get; set; }
}

public sealed class AzureOpenAIConfiguration
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string ApiVersion { get; set; } = "";
}

/// <summary>
/// Application Insights configuration.
/// </summary>
public sealed class AppInsightsConfiguration
{
    [Required]
    public string WorkspaceId { get; set; } = "";
    public bool UseManagedIdentity { get; set; } = true;
    public string? ConnectionString { get; set; }
    public string ResourceName { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
}

/// <summary>
/// GitHub configuration for reading code and creating PRs.
/// </summary>
public sealed class GitHubConfiguration
{
    [Required] public string Token { get; set; } = "";
    [Required] public string Owner { get; set; } = "";
    [Required] public string Repo { get; set; } = "";
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
