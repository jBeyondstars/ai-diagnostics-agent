using Agent.Core.Configuration;
using Agent.Core.Models;
using Agent.Core.Services;
using Agent.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Json;

namespace Agent.Core;

/// <summary>
/// The main Diagnostics Agent that orchestrates exception analysis and code fixing.
/// Uses Semantic Kernel Agents Framework GA (1.45+) with modern C# 14 patterns.
/// </summary>
public sealed class DiagnosticsAgent(
    IOptions<AgentConfiguration> config,
    ILogger<DiagnosticsAgent> logger,
    DeduplicationService? deduplicationService = null) : IDisposable
{
    private readonly AgentConfiguration _config = config.Value;
    private readonly Kernel _kernel = CreateKernel(config.Value, logger);
    private readonly DeduplicationService? _deduplicationService = deduplicationService;
    private ChatCompletionAgent? _agent;
    private bool _disposed;

    private AppInsightsPlugin? _appInsightsPlugin;
    private GitHubPlugin? _gitHubPlugin;

    private const string SystemPrompt = """
        You are an expert DevOps engineer and .NET developer. Your job is to analyze
        production exceptions from Application Insights and propose code fixes.

        ## Your Capabilities

        You have access to these tools:
        - **AppInsights**: Query exceptions, failed requests, dependency failures, and traces
        - **GitHub**: Read source code, search for files, and create Pull Requests

        ## Your Workflow

        1. **Discover**: Use `get_exceptions` to find the most critical exceptions
        2. **Investigate**: For each critical exception:
           - Analyze the stack trace to identify the source file and line
           - Use `get_file_content` to read the relevant code
           - Use `search_code` if you need to find related files
        3. **Diagnose**: Determine the root cause of each exception
        4. **Fix**: Generate corrected code that fixes the issue
        5. **Document**: Create a PR with clear explanations

        ## Guidelines

        - Focus on exceptions with HIGH occurrence counts (>10) first
        - Always read the actual source code before proposing a fix
        - Consider null checks, exception handling, and edge cases
        - Provide clear explanations of what was wrong and how you fixed it
        - If you're not confident about a fix, create an Issue instead of a PR
        - Never make changes that could break existing functionality
        - Follow C# best practices and the existing code style

        ## Output Format

        When generating fixes, always explain:
        1. What exception was occurring and why
        2. What the root cause is in the code
        3. How your fix addresses the root cause
        4. Any tests that should be added

        Be thorough but concise. Focus on actionable fixes.
        """;

    /// <summary>
    /// Creates the Semantic Kernel with the configured LLM provider
    /// </summary>
    private static Kernel CreateKernel(AgentConfiguration config, ILogger logger)
    {
        var builder = Kernel.CreateBuilder();

        switch (config.Llm.Provider.ToLowerInvariant())
        {
            case "anthropic":
            case "claude":
                logger.LogInformation("Using Anthropic Claude with model {Model}", config.Llm.Anthropic.Model);
                var anthropicService = new AnthropicChatCompletionService(
                    config.Llm.Anthropic.ApiKey,
                    config.Llm.Anthropic.Model,
                    logger);
                builder.Services.AddSingleton<IChatCompletionService>(anthropicService);
                break;

            case "openai":
                logger.LogInformation("Using OpenAI provider with model {Model}", config.Llm.OpenAI.Model);
                builder.AddOpenAIChatCompletion(config.Llm.OpenAI.Model, config.Llm.OpenAI.ApiKey);
                break;

            case "azureopenai":
                logger.LogInformation("Using Azure OpenAI provider with deployment {Deployment}",
                    config.Llm.AzureOpenAI.DeploymentName);
                builder.AddAzureOpenAIChatCompletion(
                    config.Llm.AzureOpenAI.DeploymentName,
                    config.Llm.AzureOpenAI.Endpoint,
                    config.Llm.AzureOpenAI.ApiKey);
                break;

            default:
                throw new ArgumentException($"Unknown LLM provider: {config.Llm.Provider}. Supported: Anthropic, OpenAI, AzureOpenAI");
        }

        return builder.Build();
    }

    /// <summary>
    /// Gets or creates the ChatCompletionAgent with plugins
    /// </summary>
    private ChatCompletionAgent GetOrCreateAgent()
    {
        if (_agent is not null) return _agent;

        _appInsightsPlugin = new AppInsightsPlugin(
            _config.AppInsights.WorkspaceId,
            logger as ILogger<AppInsightsPlugin>);
        _kernel.ImportPluginFromObject(_appInsightsPlugin, "AppInsights");

        _gitHubPlugin = new GitHubPlugin(
            _config.GitHub.Token,
            _config.GitHub.Owner,
            _config.GitHub.Repo,
            _config.GitHub.DefaultBranch,
            logger as ILogger<GitHubPlugin>);
        _kernel.ImportPluginFromObject(_gitHubPlugin, "GitHub");

        logger.LogInformation("Registered plugins: AppInsights, GitHub ({Owner}/{Repo})",
            _config.GitHub.Owner, _config.GitHub.Repo);

#pragma warning disable SKEXP0110
        _agent = new ChatCompletionAgent
        {
            Name = "DiagnosticsAgent",
            Instructions = SystemPrompt,
            Kernel = _kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
                Temperature = 0.1f,
                MaxTokens = 4096
            })
        };
#pragma warning restore SKEXP0110

        return _agent;
    }

    /// <summary>
    /// Runs a full analysis using 2-phase architecture:
    /// Phase 1: Prefetch all data (exceptions + source files) in parallel
    /// Phase 2: Single Claude call with complete context
    /// </summary>
    public async Task<DiagnosticReport> AnalyzeAndFixAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Starting 2-phase analysis: {Hours}h lookback, CreatePR={CreatePR}, MaxExceptions={Max}",
            request.HoursToAnalyze,
            request.CreatePullRequest,
            request.MaxExceptionsToAnalyze);

        GetOrCreateAgent();

        // PHASE 1: Prefetch all data in parallel (no Claude calls)
        logger.LogInformation("Phase 1: Prefetching data from AppInsights and GitHub...");

        // 1a. Get exceptions from AppInsights
        string exceptionsJson;
        if (request.LatestOnly)
        {
            logger.LogInformation("LatestOnly mode: fetching only the most recent exception");
            exceptionsJson = await _appInsightsPlugin!.GetLatestExceptionAsync(request.HoursToAnalyze);
        }
        else
        {
            exceptionsJson = await _appInsightsPlugin!.GetExceptionsAsync(
                request.HoursToAnalyze,
                request.MinOccurrences,
                request.MaxExceptionsToAnalyze);
        }

        var exceptions = ParseExceptionsFromJson(exceptionsJson);
        logger.LogInformation("Found {Count} exceptions to analyze", exceptions.Count);

        if (exceptions.Count == 0)
        {
            return new DiagnosticReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                AnalysisPeriod = TimeSpan.FromHours(request.HoursToAnalyze),
                Summary = "No exceptions found matching the criteria.",
                Exceptions = [],
                ProposedFixes = []
            };
        }

        // Filter out non-actionable exceptions (infrastructure/transient errors)
        var exceptionFilter = _config.ExceptionFilter;
        var actionableExceptions = new List<ExceptionInfo>();

        foreach (var ex in exceptions)
        {
            var filterResult = IsNonActionableException(ex, exceptionFilter);

            if (filterResult.IsFiltered)
            {
                logger.LogInformation("Skipping non-actionable exception {Type}: {Reason}",
                    ex.ExceptionType, filterResult.Reason);
                continue;
            }

            // For ambiguous exceptions, optionally ask Claude to evaluate
            if (filterResult.IsAmbiguous && exceptionFilter.EnableClaudeEvaluation)
            {
                var isFixable = await EvaluateIfFixableAsync(ex, cancellationToken);
                if (!isFixable)
                {
                    logger.LogInformation("Claude determined {Type} is not fixable by code changes",
                        ex.ExceptionType);
                    continue;
                }
                logger.LogInformation("Claude determined {Type} IS fixable by code changes",
                    ex.ExceptionType);
            }

            actionableExceptions.Add(ex);
        }

        if (actionableExceptions.Count == 0)
        {
            logger.LogInformation("All {Count} exceptions were filtered as non-actionable", exceptions.Count);
            return new DiagnosticReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                AnalysisPeriod = TimeSpan.FromHours(request.HoursToAnalyze),
                Summary = $"Found {exceptions.Count} exceptions but all were infrastructure/transient errors with no code fix available.",
                Exceptions = exceptions,
                ProposedFixes = []
            };
        }

        logger.LogInformation("After filtering: {Actionable}/{Total} actionable exceptions",
            actionableExceptions.Count, exceptions.Count);
        exceptions = actionableExceptions;

        // Filter out exceptions that were recently analyzed (Redis deduplication)
        if (_deduplicationService != null)
        {
            var filteredExceptions = new List<ExceptionInfo>();
            foreach (var ex in exceptions)
            {
                var shouldAnalyze = await _deduplicationService.ShouldAnalyzeAsync(
                    ex.ProblemId ?? ex.ExceptionType,
                    cancellationToken);

                if (shouldAnalyze)
                {
                    filteredExceptions.Add(ex);
                }
                else
                {
                    logger.LogInformation("Skipping {Type} (problemId: {ProblemId}) - recently analyzed",
                        ex.ExceptionType, ex.ProblemId);
                }
            }

            if (filteredExceptions.Count == 0)
            {
                logger.LogInformation("All exceptions were recently analyzed, nothing new to process");
                return new DiagnosticReport
                {
                    GeneratedAt = DateTimeOffset.UtcNow,
                    AnalysisPeriod = TimeSpan.FromHours(request.HoursToAnalyze),
                    Summary = "All exceptions were recently analyzed. No new issues to process.",
                    Exceptions = exceptions,
                    ProposedFixes = []
                };
            }

            logger.LogInformation("After deduplication: {New}/{Total} exceptions to analyze",
                filteredExceptions.Count, exceptions.Count);
            exceptions = filteredExceptions;
        }

        // 1b. Extract source file paths from stack traces
        var sourceFiles = exceptions
            .Where(e => !string.IsNullOrEmpty(e.SourceFile))
            .Select(e => e.SourceFile!)
            .Distinct()
            .Take(10)
            .ToList();

        logger.LogInformation("Identified {Count} source files to fetch", sourceFiles.Count);

        // 1c. Fetch all source files in parallel
        var fileContents = new Dictionary<string, string>();
        if (sourceFiles.Count > 0)
        {
            var fileTasks = sourceFiles.Select(async file =>
            {
                try
                {
                    var json = await _gitHubPlugin!.GetFileContentAsync(file);

                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("content", out var contentProp))
                    {
                        var content = contentProp.GetString() ?? "";
                        logger.LogDebug("Fetched {File}: {Length} chars", file, content.Length);
                        return (file, content);
                    }
                    if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    {
                        var error = errorProp.GetString() ?? "Unknown error";
                        logger.LogWarning("GitHub returned error for {File}: {Error}", file, error);
                        return (file, $"Error: {error}");
                    }
                    return (file, "Error: Invalid response format from GitHub");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to fetch file: {File}", file);
                    return (file, $"Error: {ex.Message}");
                }
            });

            var results = await Task.WhenAll(fileTasks);
            foreach (var (file, content) in results)
            {
                fileContents[file] = content.Length > 15000
                    ? content[..15000] + "\n... (truncated)"
                    : content;

                var preview = content.Length > 200 ? content[..200] : content;
                var isError = content.StartsWith("Error:");
                logger.LogInformation("File {File}: {Length} chars, isError={IsError}, preview: {Preview}",
                    file, content.Length, isError, preview.Replace("\n", "\\n"));
            }
        }

        logger.LogInformation("Phase 1 complete. Fetched {FileCount} files.", fileContents.Count);

        // PHASE 2: Single call with all context
        logger.LogInformation("Phase 2: Analyzing with Claude (single call)...");

        var contextBuilder = new System.Text.StringBuilder();
        contextBuilder.AppendLine("# Exception Analysis Context");
        contextBuilder.AppendLine();
        contextBuilder.AppendLine("## Exceptions from Application Insights");
        contextBuilder.AppendLine("```json");
        contextBuilder.AppendLine(exceptionsJson);
        contextBuilder.AppendLine("```");
        contextBuilder.AppendLine();

        if (fileContents.Count > 0)
        {
            contextBuilder.AppendLine("## Source Code Files (with line numbers)");
            foreach (var (file, content) in fileContents)
            {
                contextBuilder.AppendLine($"### {file}");
                contextBuilder.AppendLine("```csharp");
                var lines = content.Split('\n');
                logger.LogInformation("Adding {LineCount} lines from {File} to context", lines.Length, file);
                for (int i = 0; i < lines.Length; i++)
                {
                    contextBuilder.AppendLine($"{i + 1,4}: {lines[i]}");
                }
                contextBuilder.AppendLine("```");
                contextBuilder.AppendLine();
            }
            logger.LogInformation("Total context size: {Size} chars", contextBuilder.Length);
        }

        var prompt = $$"""
            You are an expert .NET developer. Analyze these production exceptions and propose fixes.

            {{contextBuilder}}

            ## Your Task
            1. Look at the exception type and method name in the stack trace
            2. Find that method in the source code above (lines are numbered: "  123: code")
            3. Identify the buggy line(s) that cause the exception
            4. Propose a fix

            ## CRITICAL: How to specify originalCode
            The source code has line numbers. To specify originalCode:
            1. Find the buggy line (e.g., line 270)
            2. Copy the EXACT code after the line number, preserving ALL whitespace
            3. Include 1-3 surrounding lines for context

            Example - if you see:
            ```
             269:         var results = _users.Values.Where(...).ToList();
             270:         var firstResult = results[0];
             271:         logger.LogInformation("First match: {Name}", firstResult.Name);
            ```
            Then originalCode should be:
            "        var firstResult = results[0];\n        logger.LogInformation(\"First match: {Name}\", firstResult.Name);"

            DO NOT write comments like "// Line 270..." - copy the ACTUAL code!

            Respond with JSON:
            ```json
            {
                "summary": "Brief summary",
                "exceptions": [{"type": "...", "occurrences": 1, "severity": "High", "rootCause": "..."}],
                "fixes": [{"filePath": "...", "originalCode": "ACTUAL code copied from above", "fixedCode": "fixed version", "explanation": "...", "severity": "High", "confidence": "High"}]
            }
            ```
            """;

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage(prompt);

        // Phase 2: NO TOOLS - Claude analyzes prefetched context only (single call)
        var response = await chatService.GetChatMessageContentsAsync(
            chatHistory,
            kernel: null,
            cancellationToken: cancellationToken);

        var fullResponse = string.Join("", response.Select(r => r.Content));
        logger.LogDebug("Claude response: {Response}", fullResponse);

        var report = ParseReport(fullResponse);

        report = report with
        {
            Exceptions = exceptions,
            AnalysisPeriod = TimeSpan.FromHours(request.HoursToAnalyze)
        };

        logger.LogInformation(
            "Analysis complete: {Exceptions} exceptions, {Fixes} fixes proposed",
            report.Exceptions.Count,
            report.ProposedFixes.Count);

        // PHASE 3: Create PR if requested and fixes are available
        if (request.CreatePullRequest && report.ProposedFixes.Count > 0)
        {
            logger.LogInformation("Phase 3: Creating Pull Request...");
            try
            {
                var prUrl = await CreatePullRequestAsync(report, cancellationToken);
                if (!string.IsNullOrEmpty(prUrl))
                {
                    report = report with { PullRequestUrl = prUrl };
                    logger.LogInformation("PR created: {Url}", prUrl);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to create PR");
            }
        }

        return report;
    }

    /// <summary>
    /// Parses exceptions from the AppInsights JSON response
    /// </summary>
    private List<ExceptionInfo> ParseExceptionsFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("error", out _))
                {
                    logger.LogWarning("AppInsights returned error: {Json}", json);
                }
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
                    LineNumber = item.TryGetProperty("LineNumber", out var ln) && ln.ValueKind == JsonValueKind.Number
                        ? ln.GetInt32() : null
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

    /// <summary>
    /// Creates a GitHub Pull Request with the proposed fixes
    /// </summary>
    private async Task<string?> CreatePullRequestAsync(DiagnosticReport report, CancellationToken cancellationToken)
    {
        if (_gitHubPlugin is null || report.ProposedFixes.Count == 0)
            return null;

        var fixes = report.ProposedFixes;
        var exceptions = report.Exceptions;

        // Check if there's already an open PR for this specific exception
        // Uses hybrid check: ProblemId + source file + exception type
        if (exceptions.Count > 0)
        {
            var mainException = exceptions[0];
            var existingPr = await _gitHubPlugin.CheckExistingPrAsync(
                mainException.ExceptionType,
                mainException.ProblemId,
                mainException.SourceFile);

            if (existingPr.Exists)
            {
                logger.LogInformation("PR already exists for {ExceptionType} in {SourceFile}: {Url}",
                    mainException.ExceptionType, mainException.SourceFile, existingPr.PrUrl);

                // Mark as analyzed to prevent re-analysis
                if (_deduplicationService != null)
                {
                    await _deduplicationService.MarkAsAnalyzedAsync(
                        mainException.ProblemId ?? mainException.ExceptionType,
                        TimeSpan.FromHours(24),
                        cancellationToken);
                }

                return existingPr.PrUrl;
            }
        }

        var fileChanges = new List<object>();
        var fixesByFile = fixes.GroupBy(f => f.FilePath);

        foreach (var fileGroup in fixesByFile)
        {
            var filePath = fileGroup.Key;
            if (string.IsNullOrEmpty(filePath)) continue;

            logger.LogInformation("Processing fixes for file: {Path}", filePath);

            try
            {
                var json = await _gitHubPlugin.GetFileContentAsync(filePath);

                string currentContent;
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    {
                        logger.LogWarning("Could not fetch file for PR: {Path}, Error: {Error}",
                            filePath, errorProp.GetString());
                        continue;
                    }
                    if (!doc.RootElement.TryGetProperty("content", out var contentProp))
                    {
                        logger.LogWarning("Invalid GitHub response for {Path}: no content property", filePath);
                        continue;
                    }
                    currentContent = contentProp.GetString() ?? "";
                }

                logger.LogInformation("Fetched {Path}: {Length} chars for PR", filePath, currentContent.Length);

                var newContent = currentContent;
                var lines = currentContent.Split('\n').ToList();

                foreach (var fix in fileGroup)
                {
                    logger.LogInformation("Attempting fix - OriginalCode ({OLen} chars): {Original}",
                        fix.OriginalCode?.Length ?? 0,
                        fix.OriginalCode?.Length > 100 ? fix.OriginalCode[..100] + "..." : fix.OriginalCode);

                    var isPlaceholder = fix.OriginalCode?.Contains("// Unable") == true ||
                        fix.OriginalCode?.Contains("// Line") == true ||
                        fix.OriginalCode?.Contains("// The") == true ||
                        fix.OriginalCode?.Contains("Line 270") == true ||
                        fix.OriginalCode?.Contains("not visible") == true ||
                        fix.OriginalCode?.Contains("Unable to") == true ||
                        fix.OriginalCode?.Contains("only contains") == true ||
                        fix.OriginalCode?.Contains("Note:") == true ||
                        fix.OriginalCode?.Contains("mismatch") == true ||
                        fix.OriginalCode?.StartsWith("//") == true ||
                        fix.OriginalCode?.StartsWith("Note") == true;

                    if (isPlaceholder)
                    {
                        var matchingException = report.Exceptions.FirstOrDefault(e =>
                            e.SourceFile == filePath || filePath.EndsWith(e.SourceFile ?? ""));

                        if (matchingException?.LineNumber is int lineNum && lineNum > 0 && lineNum <= lines.Count)
                        {
                            logger.LogInformation("Using fallback: applying fix at line {LineNum}", lineNum);

                            var buggyLine = lines[lineNum - 1]; // 0-indexed
                            logger.LogInformation("Buggy line content: {Line}", buggyLine.Trim());

                            var indexMatch = System.Text.RegularExpressions.Regex.Match(buggyLine, @"(\w+)\s*\[");
                            if (indexMatch.Success)
                            {
                                var varName = indexMatch.Groups[1].Value;
                                var indent = new string(' ', buggyLine.TakeWhile(char.IsWhiteSpace).Count());

                                var checkLine = $"{indent}if ({varName}.Count == 0) {{ logger.LogWarning(\"No items in {varName}\"); return Ok(new {{ message = \"No results found\" }}); }}";

                                lines.Insert(lineNum - 1, checkLine);
                                newContent = string.Join('\n', lines);
                                logger.LogInformation("Applied fallback fix: added bounds check for '{VarName}' at line {LineNum}", varName, lineNum);
                            }
                            else
                            {
                                logger.LogWarning("Could not identify index access pattern in line {LineNum}", lineNum);
                            }
                        }
                        continue;
                    }

                    if (!string.IsNullOrEmpty(fix.OriginalCode) && !string.IsNullOrEmpty(fix.FixedCode))
                    {
                        if (newContent.Contains(fix.OriginalCode))
                        {
                            newContent = newContent.Replace(fix.OriginalCode, fix.FixedCode);
                            logger.LogInformation("Successfully applied fix for {Path}", filePath);
                        }
                        else
                        {
                            logger.LogWarning("Could not find original code in {Path}. Looking for: {Code}",
                                filePath, fix.OriginalCode?.Length > 50 ? fix.OriginalCode[..50] + "..." : fix.OriginalCode);
                        }
                    }
                }

                if (newContent != currentContent)
                {
                    fileChanges.Add(new { Path = filePath, Content = newContent });
                    logger.LogInformation("File {Path} will be included in PR", filePath);
                }
                else
                {
                    logger.LogWarning("No changes applied to {Path} - content unchanged", filePath);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process file for PR: {Path}", filePath);
            }
        }

        if (fileChanges.Count == 0)
        {
            logger.LogWarning("No file changes to include in PR");
            return null;
        }

        var title = exceptions.Count == 1
            ? $"fix: Handle {exceptions[0].ExceptionType.Split('.').Last()}"
            : $"fix: Address {exceptions.Count} production exceptions";

        var descBuilder = new System.Text.StringBuilder();
        descBuilder.AppendLine("## Summary");
        descBuilder.AppendLine(report.Summary);
        descBuilder.AppendLine();
        descBuilder.AppendLine("## Changes");
        foreach (var fix in fixes.Where(f => fileChanges.Any(fc => ((dynamic)fc).Path == f.FilePath)))
        {
            descBuilder.AppendLine($"- **{fix.FilePath}**: {fix.Explanation.Split('.')[0]}");
        }
        descBuilder.AppendLine();
        descBuilder.AppendLine("## Exceptions Fixed");
        foreach (var ex in exceptions.Take(5))
        {
            descBuilder.AppendLine($"- `{ex.ExceptionType}` ({ex.OccurrenceCount} occurrences)");
            if (!string.IsNullOrEmpty(ex.SourceFile))
            {
                descBuilder.AppendLine($"  - Source: `{ex.SourceFile}`");
            }
            if (!string.IsNullOrEmpty(ex.ProblemId))
            {
                descBuilder.AppendLine($"  - ProblemId: `{ex.ProblemId}`");
            }
        }
        descBuilder.AppendLine();
        descBuilder.AppendLine("---");
        descBuilder.AppendLine("*Generated by AI Diagnostics Agent*");

        try
        {
            var filesJson = JsonSerializer.Serialize(fileChanges);
            logger.LogInformation("Creating PR with title: {Title}, {FileCount} file changes", title, fileChanges.Count);

            var result = await _gitHubPlugin.CreatePullRequestAsync(title, descBuilder.ToString(), filesJson);
            logger.LogInformation("GitHub CreatePR response: {Response}", result?.Length > 500 ? result[..500] + "..." : result);

            string? prUrl = null;

            try
            {
                using var doc = JsonDocument.Parse(result);
                if (doc.RootElement.TryGetProperty("pullRequestUrl", out var urlProp))
                {
                    prUrl = urlProp.GetString();
                }
                else if (doc.RootElement.TryGetProperty("html_url", out var htmlUrlProp))
                {
                    prUrl = htmlUrlProp.GetString();
                }
                else if (doc.RootElement.TryGetProperty("error", out var errorProp))
                {
                    logger.LogWarning("GitHub returned error: {Error}", errorProp.GetString());
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse GitHub response as JSON");
            }

            // Try regex if JSON parsing didn't find URL
            if (string.IsNullOrEmpty(prUrl))
            {
                var urlMatch = System.Text.RegularExpressions.Regex.Match(result ?? "", @"https://github\.com/[^\s""]+/pull/\d+");
                if (urlMatch.Success)
                {
                    prUrl = urlMatch.Value;
                }
            }

            if (!string.IsNullOrEmpty(prUrl))
            {
                logger.LogInformation("PR created successfully: {Url}", prUrl);

                // Mark all exceptions as analyzed after successful PR creation
                if (_deduplicationService != null)
                {
                    foreach (var ex in exceptions)
                    {
                        await _deduplicationService.MarkAsAnalyzedAsync(
                            ex.ProblemId ?? ex.ExceptionType,
                            TimeSpan.FromHours(6),
                            cancellationToken);
                    }
                    logger.LogInformation("Marked {Count} exceptions as analyzed in Redis", exceptions.Count);
                }

                return prUrl;
            }

            logger.LogWarning("Could not extract PR URL from response");
            return result?.Contains("error") == true ? null : result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create pull request");
            return null;
        }
    }

    /// <summary>
    /// Streams the analysis progress in real-time using IAsyncEnumerable
    /// </summary>
    public async IAsyncEnumerable<string> AnalyzeStreamingAsync(
        AnalysisRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var agent = GetOrCreateAgent();

#pragma warning disable SKEXP0110
        var chatHistory = new ChatHistory();
#pragma warning restore SKEXP0110

        chatHistory.AddUserMessage($$"""
            Analyze exceptions from the last {{request.HoursToAnalyze}} hours.
            Focus on critical exceptions (>{{request.MinOccurrences}} occurrences).
            Explain your reasoning step by step as you work.
            """);

#pragma warning disable SKEXP0110
        await foreach (var response in agent.InvokeStreamingAsync(chatHistory, cancellationToken: cancellationToken))
        {
            var content = response.Message?.Content ?? response.ToString();
            if (!string.IsNullOrEmpty(content))
            {
                yield return content;
            }
        }
#pragma warning restore SKEXP0110
    }

    /// <summary>
    /// Analyzes a single exception in detail
    /// </summary>
    public async Task<CodeFix?> AnalyzeSingleExceptionAsync(
        ExceptionInfo exception,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Analyzing single exception: {Type}", exception.ExceptionType);

        var agent = GetOrCreateAgent();

#pragma warning disable SKEXP0110
        var chatHistory = new ChatHistory();
#pragma warning restore SKEXP0110

        chatHistory.AddUserMessage($$"""
            Analyze this specific exception and propose a fix:

            **Exception Type**: {{exception.ExceptionType}}
            **Message**: {{exception.Message}}
            **Occurrences**: {{exception.OccurrenceCount}}
            **Operation**: {{exception.OperationName}}
            **Source File**: {{exception.SourceFile ?? "Unknown"}}
            **Line**: {{exception.LineNumber?.ToString() ?? "Unknown"}}

            **Stack Trace**:
            ```
            {{exception.StackTrace}}
            ```

            1. First, retrieve the source file using get_file_content
            2. Analyze the code around line {{exception.LineNumber}}
            3. Identify the root cause
            4. Propose a specific fix

            Respond with a JSON object:
            ```json
            {
                "filePath": "path/to/file.cs",
                "originalCode": "the problematic code",
                "fixedCode": "the corrected code",
                "explanation": "why this fixes the issue",
                "confidence": "High|Medium|Low"
            }
            ```
            """);

#pragma warning disable SKEXP0110
        var responses = new List<string>();
        await foreach (var response in agent.InvokeAsync(chatHistory, cancellationToken: cancellationToken))
        {
            var content = response.Message?.Content ?? response.ToString();
            responses.Add(content ?? "");
        }
#pragma warning restore SKEXP0110

        return ParseCodeFix(string.Join("", responses), exception);
    }

    #region Parsing Helpers

    private DiagnosticReport ParseReport(string content)
    {
        try
        {
            var json = ExtractJson(content);

            var parsed = JsonSerializer.Deserialize<ReportDto>(json, JsonOptions);

            return new DiagnosticReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                AnalysisPeriod = TimeSpan.FromHours(24),
                Summary = parsed?.Summary ?? "Analysis completed",
                PullRequestUrl = parsed?.PullRequestUrl,
                Exceptions = parsed?.Exceptions?.Select(e => new ExceptionInfo
                {
                    ExceptionType = e.Type ?? "Unknown",
                    Message = e.RootCause ?? "",
                    StackTrace = "",
                    OperationName = "",
                    Timestamp = DateTimeOffset.UtcNow,
                    OccurrenceCount = e.Occurrences
                }).ToList() ?? [],
                ProposedFixes = parsed?.Fixes?.Select(f => new CodeFix
                {
                    FilePath = f.FilePath ?? "",
                    OriginalCode = f.OriginalCode ?? "",
                    FixedCode = f.FixedCode ?? "",
                    Explanation = f.Explanation ?? "",
                    Severity = f.Severity ?? "Medium",
                    Confidence = f.Confidence ?? "Medium",
                    RelatedExceptions = []
                }).ToList() ?? []
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse report JSON, returning empty report");
            return new DiagnosticReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                AnalysisPeriod = TimeSpan.FromHours(24),
                Summary = content.Length > 500 ? content[..500] : content,
                Exceptions = [],
                ProposedFixes = []
            };
        }
    }

    private CodeFix? ParseCodeFix(string? content, ExceptionInfo exception)
    {
        if (string.IsNullOrEmpty(content)) return null;

        try
        {
            var json = ExtractJson(content);
            var parsed = JsonSerializer.Deserialize<FixDto>(json, JsonOptions);

            if (parsed is null || string.IsNullOrEmpty(parsed.FixedCode)) return null;

            return new CodeFix
            {
                FilePath = parsed.FilePath ?? exception.SourceFile ?? "",
                OriginalCode = parsed.OriginalCode ?? "",
                FixedCode = parsed.FixedCode,
                Explanation = parsed.Explanation ?? "",
                Severity = exception.Severity,
                Confidence = parsed.Confidence ?? "Medium",
                RelatedExceptions = [exception.ExceptionType]
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractJson(string content)
    {
        var jsonBlockStart = content.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (jsonBlockStart >= 0)
        {
            var start = content.IndexOf('\n', jsonBlockStart) + 1;
            var end = content.IndexOf("```", start);
            if (end > start)
            {
                return content[start..end].Trim();
            }
        }

        var firstBrace = content.IndexOf('{');
        var lastBrace = content.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return content[firstBrace..(lastBrace + 1)];
        }

        return "{}";
    }

    private static string? ExtractUrl(string? content, string domain)
    {
        if (string.IsNullOrEmpty(content)) return null;

        var pattern = $@"https?://{domain}[^\s\)""']+";
        var match = System.Text.RegularExpressions.Regex.Match(content, pattern);
        return match.Success ? match.Value : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    #endregion

    #region DTOs

    private sealed record ReportDto(
        string? Summary,
        string? PullRequestUrl,
        int? ExceptionsAnalyzed,
        int? FixesProposed,
        List<ExceptionDto>? Exceptions,
        List<FixDto>? Fixes);

    private sealed record ExceptionDto(
        string? Type,
        int Occurrences,
        string? Severity,
        string? RootCause);

    private sealed record FixDto(
        string? FilePath,
        string? OriginalCode,
        string? FixedCode,
        string? Explanation,
        string? Severity,
        string? Confidence);

    #endregion

    #region Exception Filtering

    /// <summary>
    /// Result of exception filtering check
    /// </summary>
    private readonly record struct ExceptionFilterResult(bool IsFiltered, bool IsAmbiguous, string? Reason);

    /// <summary>
    /// Checks if an exception is non-actionable (infrastructure/transient error with no code fix).
    /// </summary>
    private static ExceptionFilterResult IsNonActionableException(ExceptionInfo exception, ExceptionFilterConfiguration filter)
    {
        var exceptionType = exception.ExceptionType;
        var message = exception.Message ?? "";

        // Level 1: Check exact type matches
        if (filter.ExcludedTypes.Any(t => exceptionType.Equals(t, StringComparison.OrdinalIgnoreCase)))
        {
            return new ExceptionFilterResult(IsFiltered: true, IsAmbiguous: false, Reason: "Excluded exception type");
        }

        // Level 2: Check pattern matches (partial string match)
        var matchedPattern = filter.ExcludedPatterns.FirstOrDefault(p =>
            exceptionType.Contains(p, StringComparison.OrdinalIgnoreCase) ||
            message.Contains(p, StringComparison.OrdinalIgnoreCase));

        if (matchedPattern != null)
        {
            return new ExceptionFilterResult(IsFiltered: true, IsAmbiguous: false, Reason: $"Matched pattern: {matchedPattern}");
        }

        // Check for ambiguous cases that might need Claude evaluation
        var ambiguousIndicators = new[]
        {
            "external", "api", "service", "endpoint", "remote", "connection",
            "authentication", "authorization", "401", "403", "500", "502", "503", "504"
        };

        var isAmbiguous = ambiguousIndicators.Any(indicator =>
            exceptionType.Contains(indicator, StringComparison.OrdinalIgnoreCase) ||
            message.Contains(indicator, StringComparison.OrdinalIgnoreCase));

        return new ExceptionFilterResult(IsFiltered: false, IsAmbiguous: isAmbiguous, Reason: null);
    }

    /// <summary>
    /// Asks Claude to evaluate if an ambiguous exception is fixable by code changes.
    /// </summary>
    private async Task<bool> EvaluateIfFixableAsync(ExceptionInfo exception, CancellationToken cancellationToken)
    {
        try
        {
            var chatService = _kernel.GetRequiredService<IChatCompletionService>();
            var chatHistory = new ChatHistory();

            var prompt = $"""
                You are evaluating if a production exception can be fixed by changing the application code.

                Exception Type: {exception.ExceptionType}
                Message: {exception.Message}
                Stack Trace (first 500 chars): {exception.StackTrace?.Take(500).ToString() ?? "N/A"}
                Operation: {exception.OperationName}

                Question: Can this exception be prevented or handled better by modifying the application code?

                Consider:
                - If this is a transient infrastructure error (network timeout, service unavailable), the answer is usually NO
                - If the code is missing proper error handling, retry logic, or input validation, the answer is YES
                - If the exception reveals a bug in the business logic, the answer is YES
                - If the exception is caused by external systems being down, the answer is usually NO

                Respond with ONLY one word: YES or NO
                """;

            chatHistory.AddUserMessage(prompt);

            var response = await chatService.GetChatMessageContentsAsync(
                chatHistory,
                kernel: null,
                cancellationToken: cancellationToken);

            var answer = string.Join("", response.Select(r => r.Content)).Trim().ToUpperInvariant();
            logger.LogDebug("Claude evaluation for {Type}: {Answer}", exception.ExceptionType, answer);

            return answer.Contains("YES");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to evaluate exception fixability, defaulting to true");
            return true; // Default to analyzing if Claude evaluation fails
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Cleanup if needed
    }
}
