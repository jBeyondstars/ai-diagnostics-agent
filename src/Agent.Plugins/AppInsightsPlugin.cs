using System.Collections.Frozen;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Agent.Plugins;

/// <summary>
/// Semantic Kernel plugin for querying Application Insights.
/// This is the "eyes" of our agent - it sees what's happening in production.
/// Uses modern C# 14 patterns: primary constructors, collection expressions, FrozenDictionary.
/// </summary>
public sealed partial class AppInsightsPlugin(
    string workspaceId,
    ILogger<AppInsightsPlugin>? logger = null)
{
    private readonly LogsQueryClient _logsClient = new(new DefaultAzureCredential());
    private readonly string _workspaceId = workspaceId;
    private readonly ILogger<AppInsightsPlugin> _logger = logger
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AppInsightsPlugin>.Instance;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly FrozenSet<string> CommonPathPrefixes = new[]
    {
        "/src/", "/app/", "/home/", "/_/src/"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets aggregated exceptions from Application Insights
    /// </summary>
    [KernelFunction("get_exceptions")]
    [Description("Retrieves exceptions from the last N hours, grouped by type with occurrence counts. Returns the most frequent exceptions first.")]
    public async Task<string> GetExceptionsAsync(
        [Description("Number of hours to look back (e.g., 24 for last day)")]
        int hours = 24,
        [Description("Minimum number of occurrences to include an exception")]
        int minOccurrences = 1,
        [Description("Maximum number of exception types to return")]
        int maxResults = 20)
    {
        _logger.LogInformation("Querying exceptions for the last {Hours} hours", hours);

        var query = $"""
            exceptions
            | where timestamp > ago({hours}h)
            | summarize
                OccurrenceCount = count(),
                AffectedUsers = dcount(user_Id),
                FirstSeen = min(timestamp),
                LastSeen = max(timestamp),
                SampleMessage = take_any(outerMessage),
                SampleStackTrace = take_any(details[0].rawStack),
                SampleOperation = take_any(operation_Name)
              by type, problemId
            | where OccurrenceCount >= {minOccurrences}
            | order by OccurrenceCount desc
            | take {maxResults}
            | project
                ExceptionType = type,
                ProblemId = problemId,
                OccurrenceCount,
                AffectedUsers,
                FirstSeen,
                LastSeen,
                Message = SampleMessage,
                StackTrace = SampleStackTrace,
                OperationName = SampleOperation
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                _workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromHours(hours)));

            var results = response.Value.Table.Rows.Select(row =>
            {
                var stackTrace = row["StackTrace"]?.ToString() ?? "";
                var (sourceFile, lineNumber) = ParseStackTrace(stackTrace);

                return new
                {
                    ExceptionType = row["ExceptionType"]?.ToString(),
                    ProblemId = row["ProblemId"]?.ToString(),
                    OccurrenceCount = Convert.ToInt32(row["OccurrenceCount"]),
                    AffectedUsers = Convert.ToInt32(row["AffectedUsers"]),
                    FirstSeen = row["FirstSeen"]?.ToString(),
                    LastSeen = row["LastSeen"]?.ToString(),
                    Message = row["Message"]?.ToString(),
                    StackTrace = TruncateStackTrace(stackTrace, 2000),
                    OperationName = row["OperationName"]?.ToString(),
                    SourceFile = sourceFile,
                    LineNumber = lineNumber
                };
            }).ToList();

            _logger.LogInformation("Found {Count} exception types", results.Count);

            return JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying Application Insights");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// Gets detailed information about a specific exception type
    /// </summary>
    [KernelFunction("get_exception_details")]
    [Description("Gets detailed samples of a specific exception type, including full stack traces and custom dimensions")]
    public async Task<string> GetExceptionDetailsAsync(
        [Description("The exception type to get details for (e.g., 'System.NullReferenceException')")]
        string exceptionType,
        [Description("Number of hours to look back")]
        int hours = 24,
        [Description("Number of samples to retrieve")]
        int sampleCount = 5)
    {
        _logger.LogInformation("Getting details for {ExceptionType}", exceptionType);

        var query = $"""
            exceptions
            | where timestamp > ago({hours}h)
            | where type == "{EscapeKql(exceptionType)}"
            | order by timestamp desc
            | take {sampleCount}
            | project
                Timestamp = timestamp,
                OperationId = operation_Id,
                OperationName = operation_Name,
                Message = outerMessage,
                InnerMessage = innermostMessage,
                StackTrace = details[0].rawStack,
                CustomDimensions = customDimensions,
                UserId = user_Id,
                SessionId = session_Id
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                _workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromHours(hours)));

            var results = response.Value.Table.Rows.Select(row => new
            {
                Timestamp = row["Timestamp"]?.ToString(),
                OperationId = row["OperationId"]?.ToString(),
                OperationName = row["OperationName"]?.ToString(),
                Message = row["Message"]?.ToString(),
                InnerMessage = row["InnerMessage"]?.ToString(),
                StackTrace = row["StackTrace"]?.ToString(),
                CustomDimensions = row["CustomDimensions"]?.ToString()
            }).ToList();

            return JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting exception details");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// Gets failed HTTP requests
    /// </summary>
    [KernelFunction("get_failed_requests")]
    [Description("Gets HTTP requests that failed (status >= 400) with their error rates")]
    public async Task<string> GetFailedRequestsAsync(
        [Description("Number of hours to look back")]
        int hours = 24,
        [Description("Maximum results to return")]
        int maxResults = 20)
    {
        _logger.LogInformation("Querying failed requests for the last {Hours} hours", hours);

        var query = $"""
            requests
            | where timestamp > ago({hours}h)
            | where success == false
            | summarize
                FailureCount = count(),
                AvgDurationMs = avg(duration),
                P95DurationMs = percentile(duration, 95),
                StatusCodes = make_set(resultCode),
                SampleUrl = take_any(url)
              by name
            | order by FailureCount desc
            | take {maxResults}
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                _workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromHours(hours)));

            var results = response.Value.Table.Rows.Select(row => new
            {
                Endpoint = row["name"]?.ToString(),
                FailureCount = Convert.ToInt32(row["FailureCount"]),
                AvgDurationMs = Convert.ToDouble(row["AvgDurationMs"]),
                P95DurationMs = Convert.ToDouble(row["P95DurationMs"]),
                StatusCodes = row["StatusCodes"]?.ToString(),
                SampleUrl = row["SampleUrl"]?.ToString()
            }).ToList();

            return JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying failed requests");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// Gets dependency failures (database, HTTP calls, etc.)
    /// </summary>
    [KernelFunction("get_dependency_failures")]
    [Description("Gets failed dependency calls (database queries, external HTTP calls, etc.)")]
    public async Task<string> GetDependencyFailuresAsync(
        [Description("Number of hours to look back")]
        int hours = 24,
        [Description("Maximum results to return")]
        int maxResults = 20)
    {
        _logger.LogInformation("Querying dependency failures for the last {Hours} hours", hours);

        var query = $"""
            dependencies
            | where timestamp > ago({hours}h)
            | where success == false
            | summarize
                FailureCount = count(),
                AvgDurationMs = avg(duration),
                ResultCodes = make_set(resultCode)
              by type, target, name
            | order by FailureCount desc
            | take {maxResults}
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                _workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromHours(hours)));

            var results = response.Value.Table.Rows.Select(row => new
            {
                Type = row["type"]?.ToString(),
                Target = row["target"]?.ToString(),
                Name = row["name"]?.ToString(),
                FailureCount = Convert.ToInt32(row["FailureCount"]),
                AvgDurationMs = Convert.ToDouble(row["AvgDurationMs"]),
                ResultCodes = row["ResultCodes"]?.ToString()
            }).ToList();

            return JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying dependency failures");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// Gets the distributed trace for an operation
    /// </summary>
    [KernelFunction("get_trace")]
    [Description("Gets the full distributed trace for a specific operation ID to understand the request flow")]
    public async Task<string> GetTraceAsync(
        [Description("The operation ID to trace")]
        string operationId)
    {
        _logger.LogInformation("Getting trace for operation {OperationId}", operationId);

        var query = $"""
            union requests, dependencies, exceptions, traces
            | where operation_Id == "{EscapeKql(operationId)}"
            | order by timestamp asc
            | project
                Timestamp = timestamp,
                ItemType = itemType,
                Name = name,
                Success = success,
                Duration = duration,
                Message = message,
                Details = customDimensions
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                _workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromDays(7)));

            var results = response.Value.Table.Rows.Select(row => new
            {
                Timestamp = row["Timestamp"]?.ToString(),
                ItemType = row["ItemType"]?.ToString(),
                Name = row["Name"]?.ToString(),
                Success = row["Success"]?.ToString(),
                Duration = row["Duration"]?.ToString(),
                Message = row["Message"]?.ToString()
            }).ToList();

            return JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting trace");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// Gets performance metrics for slow operations
    /// </summary>
    [KernelFunction("get_slow_operations")]
    [Description("Gets operations with high latency (P95 > threshold) to identify performance bottlenecks")]
    public async Task<string> GetSlowOperationsAsync(
        [Description("Number of hours to look back")]
        int hours = 24,
        [Description("P95 latency threshold in milliseconds")]
        int thresholdMs = 1000,
        [Description("Maximum results to return")]
        int maxResults = 20)
    {
        _logger.LogInformation("Querying slow operations (P95 > {Threshold}ms) for the last {Hours} hours",
            thresholdMs, hours);

        var query = $"""
            requests
            | where timestamp > ago({hours}h)
            | summarize
                RequestCount = count(),
                AvgDurationMs = avg(duration),
                P50DurationMs = percentile(duration, 50),
                P95DurationMs = percentile(duration, 95),
                P99DurationMs = percentile(duration, 99),
                FailureRate = round(100.0 * countif(success == false) / count(), 2)
              by name
            | where P95DurationMs > {thresholdMs}
            | order by P95DurationMs desc
            | take {maxResults}
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                _workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromHours(hours)));

            var results = response.Value.Table.Rows.Select(row => new
            {
                Operation = row["name"]?.ToString(),
                RequestCount = Convert.ToInt32(row["RequestCount"]),
                AvgDurationMs = Math.Round(Convert.ToDouble(row["AvgDurationMs"]), 2),
                P50DurationMs = Math.Round(Convert.ToDouble(row["P50DurationMs"]), 2),
                P95DurationMs = Math.Round(Convert.ToDouble(row["P95DurationMs"]), 2),
                P99DurationMs = Math.Round(Convert.ToDouble(row["P99DurationMs"]), 2),
                FailureRate = Convert.ToDouble(row["FailureRate"])
            }).ToList();

            return JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying slow operations");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    #region Helper Methods

    private static (string? file, int? line) ParseStackTrace(string stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace)) return (null, null);

        var match = StackTraceRegex().Match(stackTrace);

        if (match.Success)
        {
            var file = match.Groups[1].Value;
            var line = int.TryParse(match.Groups[2].Value, out var l) ? l : (int?)null;
            file = NormalizeFilePath(file);
            return (file, line);
        }

        return (null, null);
    }

    private static string NormalizeFilePath(string path)
    {
        path = path.Replace('\\', '/');

        foreach (var prefix in CommonPathPrefixes)
        {
            var idx = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                return path[(idx + 1)..];
            }
        }

        return path;
    }

    private static string TruncateStackTrace(string stackTrace, int maxLength) =>
        string.IsNullOrEmpty(stackTrace) || stackTrace.Length <= maxLength
            ? stackTrace
            : $"{stackTrace[..maxLength]}\n... (truncated)";

    private static string EscapeKql(string value) =>
        value.Replace("\\", "\\\\")
             .Replace("\"", "\\\"")
             .Replace("'", "\\'");

    [GeneratedRegex(@"in\s+(.+\.cs):line\s+(\d+)", RegexOptions.Compiled)]
    private static partial Regex StackTraceRegex();

    #endregion
}
