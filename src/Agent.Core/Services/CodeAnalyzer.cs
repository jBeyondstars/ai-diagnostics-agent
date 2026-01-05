using System.Text.Json;
using Agent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Core.Services;

public class CodeAnalyzer(Kernel kernel, ILogger<CodeAnalyzer> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<SingleExceptionResult> AnalyzeAsync(
        ExceptionInfo exception,
        string? preFetchedFileContent,
        bool allowTools,
        CancellationToken ct)
    {
        var contextBuilder = BuildContext(exception, preFetchedFileContent, isHybrid: allowTools);

        var prompt = allowTools
            ? BuildHybridPrompt(contextBuilder)
            : BuildDirectPrompt(contextBuilder);

        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage(prompt);

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        
        var response = await chatService.GetChatMessageContentsAsync(
            chatHistory, 
            kernel: allowTools ? kernel : null, 
            cancellationToken: ct);

        var fullResponse = string.Join("", response.Select(r => r.Content));
        logger.LogDebug("Analysis response for {Type}: {Response}", exception.ExceptionType, fullResponse.Length > 200 ? fullResponse[..200] + "..." : fullResponse);

        return ParseResponse(fullResponse, exception);
    }

    private static string BuildContext(ExceptionInfo exception, string? fileContent, bool isHybrid)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Exception to Analyze");
        sb.AppendLine();
        sb.AppendLine($"**Type**: {exception.ExceptionType}");
        sb.AppendLine($"**Message**: {exception.Message}");
        sb.AppendLine($"**Occurrences**: {exception.OccurrenceCount}");
        sb.AppendLine($"**Operation**: {exception.OperationName}");
        sb.AppendLine($"**Source File**: {exception.SourceFile ?? "Unknown"}");
        sb.AppendLine($"**Line**: {exception.LineNumber?.ToString() ?? "Unknown"}");
        sb.AppendLine();
        sb.AppendLine("**Stack Trace**:");
        sb.AppendLine("```");
        sb.AppendLine(exception.StackTrace);
        sb.AppendLine("```");

        if (!string.IsNullOrEmpty(fileContent))
        {
            sb.AppendLine();
            var header = isHybrid
                ? $"## Source Code (Pre-fetched): {exception.SourceFile}"
                : $"## Source Code: {exception.SourceFile}";
            sb.AppendLine(header);
            sb.AppendLine("```csharp");
            var lines = fileContent.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                sb.AppendLine($"{i + 1,4}: {lines[i]}");
            }
            sb.AppendLine("```");
        }
        return sb.ToString();
    }

    private static string BuildDirectPrompt(string context)
    {
        return $$"""
            You are an expert .NET developer. Analyze this single production exception.

            {{context}}

            ## CRITICAL: Input Validation vs Actual Bug

            Determine if this exception is:

            ### INPUT VALIDATION ERROR (DO NOT FIX):
            - FormatException, ArgumentException, ArgumentNullException, ValidationException
            - The CLIENT sent invalid data (bad format, null, out of range)
            - The code is CORRECTLY rejecting bad input
            - Example: "The string 'sqd' was not recognized as a valid DateTime" = client error

            ### ACTUAL BUG (PROPOSE FIX):
            - NullReferenceException in code that should never receive null
            - FormatException when parsing INTERNAL data (not user input)
            - Missing error handling, logic errors

            ## Response Format

            Respond with JSON:
            ```json
            {
                "action": "fix" or "no_fix_needed",
                "rootCause": "explanation of the root cause",
                "severity": "High|Medium|Low",
                "fix": {
                    "filePath": "path/to/file.cs",
                    "originalCode": "EXACT code from source (copy after line numbers)",
                    "fixedCode": "corrected code",
                    "explanation": "what was wrong and how the fix addresses it",
                    "confidence": "High|Medium|Low"
                }
            }
            ```

            If action is "no_fix_needed", omit the "fix" object.
            """;
    }

    private static string BuildHybridPrompt(string context)
    {
        return $$"""
            You are an expert .NET developer. Analyze this production exception.

            {{context}}

            ## Instructions

            I have PRE-FETCHED the source code above. Analyze it to find the root cause.

            **IMPORTANT**: Only use tools if absolutely necessary:
            - Use `GitHub_get_file_content` if you need to see OTHER files (imports, base classes, related services)
            - Use `GitHub_search_code` if you need to find where a class/method is defined
            - Use `AppInsights_get_exception_details` if you need more stack trace samples
            - Do NOT call tools if the pre-fetched code is sufficient!

            ## Analysis Steps

            1. First, analyze the pre-fetched code to identify the bug
            2. If you need more context, use the tools
            3. Determine if this is an INPUT VALIDATION error (client's fault) or an ACTUAL BUG
            4. If it's a bug, propose a fix

            ## Response Format

            Respond with JSON:
            ```json
            {
                "action": "fix" or "no_fix_needed",
                "rootCause": "explanation",
                "severity": "High|Medium|Low",
                "toolsUsed": ["list of tools you called, or empty if none"],
                "fix": {
                    "filePath": "path/to/file.cs",
                    "originalCode": "EXACT code to replace",
                    "fixedCode": "corrected code",
                    "explanation": "what was wrong and how the fix addresses it",
                    "confidence": "High|Medium|Low"
                }
            }
            ```

            If action is "no_fix_needed", omit the "fix" object.
            """;
    }

    private SingleExceptionResult ParseResponse(string content, ExceptionInfo exception)
    {
        try
        {
            var json = ExtractJson(content);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var action = root.TryGetProperty("action", out var ap) ? ap.GetString() ?? "fix" : "fix";
            var rootCause = root.TryGetProperty("rootCause", out var rc) ? rc.GetString() : null;

            if (action == "no_fix_needed") return new SingleExceptionResult("no_fix_needed", rootCause, null, null);

            if (root.TryGetProperty("fix", out var fixProp) && fixProp.ValueKind == JsonValueKind.Object)
            {
                var fix = new CodeFix
                {
                    FilePath = fixProp.TryGetProperty("filePath", out var fp) ? fp.GetString() ?? "" : "",
                    OriginalCode = fixProp.TryGetProperty("originalCode", out var oc) ? oc.GetString() ?? "" : "",
                    FixedCode = fixProp.TryGetProperty("fixedCode", out var fc) ? fc.GetString() ?? "" : "",
                    Explanation = fixProp.TryGetProperty("explanation", out var ex) ? ex.GetString() ?? "" : "",
                    Confidence = fixProp.TryGetProperty("confidence", out var cf) ? cf.GetString() ?? "Medium" : "Medium",
                    Severity = root.TryGetProperty("severity", out var sv) ? sv.GetString() ?? "Medium" : "Medium",
                    RelatedExceptions = [],
                    SuggestedTests = []
                };
                return new SingleExceptionResult("fix", rootCause, fix, null);
            }

            return new SingleExceptionResult("error", "No fix provided", null, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse response");
            return new SingleExceptionResult("error", "Failed to parse response", null, null);
        }
    }

    private static string ExtractJson(string content)
    {
        var start = content.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var s = content.IndexOf('\n', start) + 1;
            var e = content.IndexOf("```", s);
            if (e > s) return content[s..e].Trim();
        }
        
        var fb = content.IndexOf('{');
        var lb = content.LastIndexOf('}');
        return (fb >= 0 && lb > fb) ? content[fb..(lb + 1)] : "{}";
    }
}

public record SingleExceptionResult(string Action, string? RootCause, CodeFix? Fix, string? PrUrl);