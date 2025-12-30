namespace Agent.Plugins;

/// <summary>
/// Abstraction for log/telemetry providers.
/// Implement this interface to support different backends:
/// - Azure Application Insights (default)
/// - Datadog
/// - Seq
/// - Elastic APM
/// - Splunk
/// - New Relic
/// - AWS CloudWatch
/// - Grafana Loki
/// </summary>
public interface ILogProvider
{
    /// <summary>
    /// Gets aggregated exceptions from the logging backend
    /// </summary>
    Task<IReadOnlyList<ExceptionSummary>> GetExceptionsAsync(
        int hoursToAnalyze,
        int minOccurrences,
        int maxResults,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed samples of a specific exception type
    /// </summary>
    Task<IReadOnlyList<ExceptionDetail>> GetExceptionDetailsAsync(
        string exceptionType,
        int hours,
        int sampleCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets failed HTTP requests
    /// </summary>
    Task<IReadOnlyList<FailedRequest>> GetFailedRequestsAsync(
        int hours,
        int maxResults,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the distributed trace for an operation
    /// </summary>
    Task<IReadOnlyList<TraceEvent>> GetTraceAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Provider name for logging/display
    /// </summary>
    string ProviderName { get; }
}

#region DTOs

public sealed record ExceptionSummary
{
    public required string ExceptionType { get; init; }
    public required string? ProblemId { get; init; }
    public required int OccurrenceCount { get; init; }
    public required int AffectedUsers { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
    public required string? Message { get; init; }
    public required string? StackTrace { get; init; }
    public required string? OperationName { get; init; }
    public string? SourceFile { get; init; }
    public int? LineNumber { get; init; }
}

public sealed record ExceptionDetail
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string? OperationId { get; init; }
    public required string? OperationName { get; init; }
    public required string? Message { get; init; }
    public required string? InnerMessage { get; init; }
    public required string? StackTrace { get; init; }
    public Dictionary<string, string> CustomDimensions { get; init; } = [];
}

public sealed record FailedRequest
{
    public required string Endpoint { get; init; }
    public required int FailureCount { get; init; }
    public required double AvgDurationMs { get; init; }
    public required double P95DurationMs { get; init; }
    public required string[] StatusCodes { get; init; }
    public string? SampleUrl { get; init; }
}

public sealed record TraceEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string ItemType { get; init; }
    public required string? Name { get; init; }
    public required bool? Success { get; init; }
    public required double? DurationMs { get; init; }
    public string? Message { get; init; }
}

#endregion
