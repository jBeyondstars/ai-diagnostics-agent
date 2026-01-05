using Azure.Identity;
using Azure.Monitor.Query;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    private readonly ILogger<AppInsightsPlugin> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AppInsightsPlugin>.Instance;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Common container path prefixes to strip
    private static readonly string[] CommonPathPrefixes =
    [
        "/_/src/",
        "/src/",
        "/app/",
        "/home/"
    ];

    /// <summary>
    /// Gets the most recent exception from Application Insights
    /// </summary>
    public async Task<string> GetLatestExceptionAsync(int hours = 1)
    {
        _logger.LogInformation("Querying latest exception from the last {Hours} hours", hours);

        var query = $"""
            AppExceptions
            | where TimeGenerated > ago({hours}h)
            | order by TimeGenerated desc
            | take 1
            | project
                ExceptionType,
                ProblemId,
                OccurrenceCount = 1,
                AffectedUsers = 1,
                FirstSeen = TimeGenerated,
                LastSeen = TimeGenerated,
                Message = OuterMessage,
                StackTrace = Details,
                OperationName,
                ItemId = _ItemId,
                Timestamp = TimeGenerated
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromHours(hours)));

            List<object> results = [.. response.Value.Table.Rows.Select(row =>
            {
                var stackTrace = row["StackTrace"]?.ToString() ?? "";
                var (sourceFile, lineNumber) = ParseStackTrace(stackTrace);

                return new
                {
                    ExceptionType = row["ExceptionType"]?.ToString(),
                    ProblemId = row["ProblemId"]?.ToString(),
                    OccurrenceCount = 1,
                    AffectedUsers = 1,
                    FirstSeen = row["FirstSeen"]?.ToString(),
                    LastSeen = row["LastSeen"]?.ToString(),
                    Message = row["Message"]?.ToString(),
                    StackTrace = TruncateStackTrace(stackTrace, 2000),
                    OperationName = row["OperationName"]?.ToString(),
                    SourceFile = sourceFile,
                    LineNumber = lineNumber,
                    ItemId = row["ItemId"]?.ToString(),
                    Timestamp = row["Timestamp"]?.ToString()
                };
            })];

            _logger.LogInformation("Found {Count} exception(s)", results.Count);

            return JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying latest exception");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

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
            AppExceptions
            | where TimeGenerated > ago({hours}h)
            | summarize
                OccurrenceCount = count(),
                AffectedUsers = dcount(UserId),
                FirstSeen = min(TimeGenerated),
                LastSeen = max(TimeGenerated),
                SampleMessage = take_any(OuterMessage),
                SampleStackTrace = take_any(Details),
                SampleOperation = take_any(OperationName),
                SampleItemId = take_any(_ItemId),
                SampleTimestamp = take_any(TimeGenerated)
              by ExceptionType, ProblemId
            | where OccurrenceCount >= {minOccurrences}
            | order by OccurrenceCount desc
            | take {maxResults}
            | project
                ExceptionType,
                ProblemId,
                OccurrenceCount,
                AffectedUsers,
                FirstSeen,
                LastSeen,
                Message = SampleMessage,
                StackTrace = SampleStackTrace,
                OperationName = SampleOperation,
                ItemId = SampleItemId,
                Timestamp = SampleTimestamp
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromHours(hours)));

            List<object> results = [.. response.Value.Table.Rows.Select(row =>
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
                    LineNumber = lineNumber,
                    ItemId = row["ItemId"]?.ToString(),
                    Timestamp = row["Timestamp"]?.ToString()
                };
            })];

            _logger.LogInformation("Found {Count} exception types", results.Count);

            return JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying Application Insights. WorkspaceId: {WorkspaceId}", workspaceId);
            return JsonSerializer.Serialize(new
            {
                error = ex.Message,
                workspaceId = workspaceId,
                innerError = ex.InnerException?.Message
            }, JsonOptions);
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
            AppExceptions
            | where TimeGenerated > ago({hours}h)
            | where ExceptionType == "{EscapeKql(exceptionType)}"
            | order by TimeGenerated desc
            | take {sampleCount}
            | project
                Timestamp = TimeGenerated,
                OperationId,
                OperationName,
                Message = OuterMessage,
                InnerMessage = InnermostMessage,
                StackTrace = Details,
                Properties,
                UserId,
                SessionId
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromHours(hours)));

            List<object> results = [.. response.Value.Table.Rows.Select(row => new
            {
                Timestamp = row["Timestamp"]?.ToString(),
                OperationId = row["OperationId"]?.ToString(),
                OperationName = row["OperationName"]?.ToString(),
                Message = row["Message"]?.ToString(),
                InnerMessage = row["InnerMessage"]?.ToString(),
                StackTrace = row["StackTrace"]?.ToString(),
                CustomDimensions = row["CustomDimensions"]?.ToString()
            })];

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
            AppRequests
            | where TimeGenerated > ago({hours}h)
            | where Success == false
            | summarize
                FailureCount = count(),
                AvgDurationMs = avg(DurationMs),
                P95DurationMs = percentile(DurationMs, 95),
                StatusCodes = make_set(ResultCode),
                SampleUrl = take_any(Url)
              by Name
            | order by FailureCount desc
            | take {maxResults}
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromHours(hours)));

            List<object> results = [.. response.Value.Table.Rows.Select(row => new
            {
                Endpoint = row["Name"]?.ToString(),
                FailureCount = Convert.ToInt32(row["FailureCount"]),
                AvgDurationMs = Convert.ToDouble(row["AvgDurationMs"]),
                P95DurationMs = Convert.ToDouble(row["P95DurationMs"]),
                StatusCodes = row["StatusCodes"]?.ToString(),
                SampleUrl = row["SampleUrl"]?.ToString()
            })];

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
            AppDependencies
            | where TimeGenerated > ago({hours}h)
            | where Success == false
            | summarize
                FailureCount = count(),
                AvgDurationMs = avg(DurationMs),
                ResultCodes = make_set(ResultCode)
              by DependencyType, Target, Name
            | order by FailureCount desc
            | take {maxResults}
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromHours(hours)));

            List<object> results = [.. response.Value.Table.Rows.Select(row => new
            {
                Type = row["DependencyType"]?.ToString(),
                Target = row["Target"]?.ToString(),
                Name = row["Name"]?.ToString(),
                FailureCount = Convert.ToInt32(row["FailureCount"]),
                AvgDurationMs = Convert.ToDouble(row["AvgDurationMs"]),
                ResultCodes = row["ResultCodes"]?.ToString()
            })];

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
            union AppRequests, AppDependencies, AppExceptions, AppTraces
            | where OperationId == "{EscapeKql(operationId)}"
            | order by TimeGenerated asc
            | project
                Timestamp = TimeGenerated,
                ItemType = Type,
                Name,
                Success,
                DurationMs,
                Message,
                Properties
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromDays(7)));

            List<object> results = [.. response.Value.Table.Rows.Select(row => new
            {
                Timestamp = row["Timestamp"]?.ToString(),
                ItemType = row["ItemType"]?.ToString(),
                Name = row["Name"]?.ToString(),
                Success = row["Success"]?.ToString(),
                DurationMs = row["DurationMs"]?.ToString(),
                Message = row["Message"]?.ToString()
            })];

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
            AppRequests
            | where TimeGenerated > ago({hours}h)
            | summarize
                RequestCount = count(),
                AvgDurationMs = avg(DurationMs),
                P50DurationMs = percentile(DurationMs, 50),
                P95DurationMs = percentile(DurationMs, 95),
                P99DurationMs = percentile(DurationMs, 99),
                FailureRate = round(100.0 * countif(Success == false) / count(), 2)
              by Name
            | where P95DurationMs > {thresholdMs}
            | order by P95DurationMs desc
            | take {maxResults}
            """;

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                workspaceId,
                query,
                new QueryTimeRange(TimeSpan.FromHours(hours)));

            List<object> results = [.. response.Value.Table.Rows.Select(row => new
            {
                Operation = row["Name"]?.ToString(),
                RequestCount = Convert.ToInt32(row["RequestCount"]),
                AvgDurationMs = Math.Round(Convert.ToDouble(row["AvgDurationMs"]), 2),
                P50DurationMs = Math.Round(Convert.ToDouble(row["P50DurationMs"]), 2),
                P95DurationMs = Math.Round(Convert.ToDouble(row["P95DurationMs"]), 2),
                P99DurationMs = Math.Round(Convert.ToDouble(row["P99DurationMs"]), 2),
                FailureRate = Convert.ToDouble(row["FailureRate"])
            })];

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

        try
        {
            using var doc = JsonDocument.Parse(stackTrace);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var exceptionObj in doc.RootElement.EnumerateArray())
                {
                    if (exceptionObj.TryGetProperty("parsedStack", out var parsedStack) &&
                        parsedStack.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var frame in parsedStack.EnumerateArray())
                        {
                            if (frame.TryGetProperty("fileName", out var fileName))
                            {
                                var fileStr = fileName.GetString();
                                if (!string.IsNullOrEmpty(fileStr) && fileStr.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                                {
                                    var normalizedFile = NormalizeFilePath(fileStr);
                                    var lineNum = frame.TryGetProperty("line", out var ln) && ln.ValueKind == JsonValueKind.Number
                                        ? ln.GetInt32()
                                        : (int?)null;
                                    return (normalizedFile, lineNum);
                                }
                            }
                        }
                    }

                    if (exceptionObj.TryGetProperty("fileName", out var directFileName))
                    {
                        var fileStr = directFileName.GetString();
                        if (!string.IsNullOrEmpty(fileStr) && fileStr.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        {
                            var normalizedFile = NormalizeFilePath(fileStr);
                            var lineNum = exceptionObj.TryGetProperty("line", out var ln) && ln.ValueKind == JsonValueKind.Number
                                ? ln.GetInt32()
                                : (int?)null;
                            return (normalizedFile, lineNum);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        var match = StackTraceRegex().Match(stackTrace);
        if (match.Success)
        {
            var file = NormalizeFilePath(match.Groups[1].Value);
            var line = int.TryParse(match.Groups[2].Value, out var l) ? l : (int?)null;
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
                // Skip the entire prefix (e.g., "/src/src/" -> "Agent.Api/...")
                return path[(idx + prefix.Length)..];
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
