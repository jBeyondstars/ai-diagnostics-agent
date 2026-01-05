using System.Text.Json;
using Agent.Core.Models;
using Microsoft.Extensions.Logging;

namespace Agent.Core.Services;

public class ExceptionsParser(ILogger<ExceptionsParser> logger)
{
    public List<ExceptionInfo> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out _))
            {
                logger.LogWarning("AppInsights returned error: {Json}", json);
                return [];
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("Unexpected JSON format (expected array): {Kind}", doc.RootElement.ValueKind);
                return [];
            }

            var exceptions = new List<ExceptionInfo>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                exceptions.Add(new ExceptionInfo
                {
                    ExceptionType = item.GetProperty("ExceptionType").GetString() ?? "Unknown",
                    Message = item.TryGetProperty("Message", out var msg) ? msg.GetString() ?? "" : "",
                    StackTrace = item.TryGetProperty("StackTrace", out var st) ? st.GetString() ?? "" : "",
                    OperationName = item.TryGetProperty("OperationName", out var op) ? op.GetString() ?? "" : "",
                    Timestamp = DateTimeOffset.UtcNow,
                    OccurrenceCount = item.TryGetProperty("OccurrenceCount", out var oc) ? oc.GetInt32() : 0,
                    ProblemId = item.TryGetProperty("ProblemId", out var pid) ? pid.GetString() : null,
                    SourceFile = item.TryGetProperty("SourceFile", out var sf) ? sf.GetString() : null,
                    LineNumber = item.TryGetProperty("LineNumber", out var ln) && ln.ValueKind == JsonValueKind.Number ? ln.GetInt32() : null,
                    ItemId = item.TryGetProperty("ItemId", out var iid) ? iid.GetString() : null,
                    ItemTimestamp = item.TryGetProperty("Timestamp", out var its) ? its.GetString() : null
                });
            }

            return exceptions;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse exceptions JSON");
            return [];
        }
    }
}
