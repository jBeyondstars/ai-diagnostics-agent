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
    /// Runs a full analysis with per-exception PR creation:
    /// Phase 1: Prefetch all exceptions and filter
    /// Phase 2: For each exception, analyze and create individual PR
    /// </summary>
    public async Task<DiagnosticReport> AnalyzeAndFixAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Starting per-exception analysis: {Hours}h lookback, CreatePR={CreatePR}, MaxExceptions={Max}",
            request.HoursToAnalyze,
            request.CreatePullRequest,
            request.MaxExceptionsToAnalyze);

        GetOrCreateAgent();

        // PHASE 1: Get and filter exceptions
        logger.LogInformation("Phase 1: Fetching exceptions from AppInsights...");

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

        var allExceptions = ParseExceptionsFromJson(exceptionsJson);
        logger.LogInformation("Found {Count} exceptions to analyze", allExceptions.Count);

        if (allExceptions.Count == 0)
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

        // Filter non-actionable exceptions
        var exceptionFilter = _config.ExceptionFilter;
        var actionableExceptions = new List<ExceptionInfo>();

        foreach (var ex in allExceptions)
        {
            var filterResult = IsNonActionableException(ex, exceptionFilter);

            if (filterResult.IsFiltered)
            {
                logger.LogInformation("Skipping non-actionable exception {Type}: {Reason}",
                    ex.ExceptionType, filterResult.Reason);
                continue;
            }

            if (filterResult.IsAmbiguous && exceptionFilter.EnableClaudeEvaluation)
            {
                var isFixable = await EvaluateIfFixableAsync(ex, cancellationToken);
                if (!isFixable)
                {
                    logger.LogInformation("Claude determined {Type} is not fixable by code changes",
                        ex.ExceptionType);
                    continue;
                }
            }

            actionableExceptions.Add(ex);
        }

        if (actionableExceptions.Count == 0)
        {
            return new DiagnosticReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                AnalysisPeriod = TimeSpan.FromHours(request.HoursToAnalyze),
                Summary = $"Found {allExceptions.Count} exceptions but all were infrastructure/transient errors.",
                Exceptions = allExceptions,
                ProposedFixes = []
            };
        }

        logger.LogInformation("After filtering: {Actionable}/{Total} actionable exceptions",
            actionableExceptions.Count, allExceptions.Count);

        // PHASE 2: Process each exception individually
        logger.LogInformation("Phase 2: Processing {Count} exceptions individually...", actionableExceptions.Count);

        var allFixes = new List<CodeFix>();
        var createdPrUrls = new List<string>();
        var skippedValidation = new List<string>();
        var skippedExistingPr = new List<string>();
        var skippedDedup = new List<string>();

        foreach (var exception in actionableExceptions)
        {
            logger.LogInformation("Processing exception: {Type} ({Count} occurrences)",
                exception.ExceptionType, exception.OccurrenceCount);

            // Check for existing PR
            if (_gitHubPlugin is not null)
            {
                var existingPr = await _gitHubPlugin.CheckExistingPrAsync(
                    exception.ExceptionType,
                    exception.ProblemId,
                    exception.SourceFile);

                if (existingPr.Exists)
                {
                    logger.LogInformation("PR already exists for {Type}: {Url}",
                        exception.ExceptionType, existingPr.PrUrl);
                    skippedExistingPr.Add($"{exception.ExceptionType} → {existingPr.PrUrl}");
                    continue;
                }
            }

            // Check deduplication
            if (_deduplicationService is not null)
            {
                var shouldAnalyze = await _deduplicationService.ShouldAnalyzeAsync(
                    exception.ProblemId ?? exception.ExceptionType,
                    cancellationToken);

                if (!shouldAnalyze)
                {
                    logger.LogInformation("Skipping {Type} - recently analyzed", exception.ExceptionType);
                    skippedDedup.Add(exception.ExceptionType);
                    continue;
                }
            }

            var result = await AnalyzeAndFixSingleExceptionAsync(
                exception,
                request.CreatePullRequest,
                cancellationToken);

            if (result.Action == "no_fix_needed")
            {
                skippedValidation.Add($"{exception.ExceptionType}: {result.RootCause}");
                continue;
            }

            if (result.Fix is not null)
            {
                allFixes.Add(result.Fix);
            }

            if (!string.IsNullOrEmpty(result.PrUrl))
            {
                createdPrUrls.Add(result.PrUrl);

                if (_deduplicationService is not null)
                {
                    var problemId = exception.ProblemId ?? exception.ExceptionType;
                    await _deduplicationService.MarkAsAnalyzedAsync(
                        problemId,
                        TimeSpan.FromHours(24),
                        cancellationToken);

                    await _deduplicationService.AddToIndexAsync(
                        exception.ExceptionType,
                        problemId,
                        cancellationToken);
                }
            }
        }

        var summaryParts = new List<string>();
        summaryParts.Add($"Analyzed {actionableExceptions.Count} exceptions");

        if (createdPrUrls.Count > 0)
            summaryParts.Add($"{createdPrUrls.Count} PRs created");
        if (skippedValidation.Count > 0)
            summaryParts.Add($"{skippedValidation.Count} input validation errors (no fix needed)");
        if (skippedExistingPr.Count > 0)
            summaryParts.Add($"{skippedExistingPr.Count} already have open PRs");
        if (skippedDedup.Count > 0)
            summaryParts.Add($"{skippedDedup.Count} recently analyzed");

        if (skippedValidation.Count > 0)
        {
            logger.LogInformation("Input validation errors skipped: {Types}",
                string.Join("; ", skippedValidation));
        }
        if (skippedExistingPr.Count > 0)
        {
            logger.LogInformation("Exceptions with existing PRs: {Types}",
                string.Join("; ", skippedExistingPr));
        }

        logger.LogInformation("Analysis complete: {Summary}", string.Join(", ", summaryParts));

        return new DiagnosticReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            AnalysisPeriod = TimeSpan.FromHours(request.HoursToAnalyze),
            Summary = string.Join(". ", summaryParts) + ".",
            Exceptions = actionableExceptions,
            ProposedFixes = allFixes,
            PullRequestUrl = createdPrUrls.Count == 1 ? createdPrUrls[0] : null,
            PullRequestUrls = createdPrUrls
        };
    }

    /// <summary>
    /// Result of analyzing a single exception
    /// </summary>
    public sealed record SingleExceptionResult(
        string Action,       // "fix", "no_fix_needed", "error"
        string? RootCause,
        CodeFix? Fix,
        string? PrUrl);

    /// <summary>
    /// Analyzes a single exception and optionally creates a PR
    /// </summary>
    private async Task<SingleExceptionResult> AnalyzeAndFixSingleExceptionAsync(
        ExceptionInfo exception,
        bool createPr,
        CancellationToken cancellationToken)
    {
        string? fileContent = null;
        if (!string.IsNullOrEmpty(exception.SourceFile) && _gitHubPlugin != null)
        {
            try
            {
                var json = await _gitHubPlugin.GetFileContentAsync(exception.SourceFile);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("content", out var contentProp))
                {
                    fileContent = contentProp.GetString();
                    if (fileContent?.Length > 15000)
                        fileContent = fileContent[..15000] + "\n... (truncated)";
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch source file: {File}", exception.SourceFile);
            }
        }

        var contextBuilder = new System.Text.StringBuilder();
        contextBuilder.AppendLine("# Exception to Analyze");
        contextBuilder.AppendLine();
        contextBuilder.AppendLine($"**Type**: {exception.ExceptionType}");
        contextBuilder.AppendLine($"**Message**: {exception.Message}");
        contextBuilder.AppendLine($"**Occurrences**: {exception.OccurrenceCount}");
        contextBuilder.AppendLine($"**Operation**: {exception.OperationName}");
        contextBuilder.AppendLine($"**Source File**: {exception.SourceFile ?? "Unknown"}");
        contextBuilder.AppendLine($"**Line**: {exception.LineNumber?.ToString() ?? "Unknown"}");
        contextBuilder.AppendLine();
        contextBuilder.AppendLine("**Stack Trace**:");
        contextBuilder.AppendLine("```");
        contextBuilder.AppendLine(exception.StackTrace);
        contextBuilder.AppendLine("```");

        if (!string.IsNullOrEmpty(fileContent))
        {
            contextBuilder.AppendLine();
            contextBuilder.AppendLine($"## Source Code: {exception.SourceFile}");
            contextBuilder.AppendLine("```csharp");
            var lines = fileContent.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                contextBuilder.AppendLine($"{i + 1,4}: {lines[i]}");
            }
            contextBuilder.AppendLine("```");
        }

        var prompt = $$"""
            You are an expert .NET developer. Analyze this single production exception.

            {{contextBuilder}}

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

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage(prompt);

        var response = await chatService.GetChatMessageContentsAsync(
            chatHistory,
            kernel: null,
            cancellationToken: cancellationToken);

        var fullResponse = string.Join("", response.Select(r => r.Content));
        logger.LogDebug("Claude response for {Type}: {Response}",
            exception.ExceptionType, fullResponse.Length > 500 ? fullResponse[..500] + "..." : fullResponse);

        var parsed = ParseSingleExceptionResponse(fullResponse);

        if (parsed.Action == "no_fix_needed")
        {
            logger.LogInformation("Exception {Type} marked as no_fix_needed: {Reason}",
                exception.ExceptionType, parsed.RootCause);
            return new SingleExceptionResult("no_fix_needed", parsed.RootCause, null, null);
        }

        if (parsed.Fix is null)
        {
            logger.LogWarning("No fix provided for {Type}", exception.ExceptionType);
            return new SingleExceptionResult("error", "No fix provided by Claude", null, null);
        }

        // Create PR if requested
        string? prUrl = null;
        if (createPr && _gitHubPlugin != null)
        {
            prUrl = await CreateSingleExceptionPrAsync(exception, parsed.Fix, cancellationToken);
        }

        return new SingleExceptionResult("fix", parsed.RootCause, parsed.Fix, prUrl);
    }

    /// <summary>
    /// Parses Claude's response for a single exception analysis
    /// </summary>
    private (string Action, string? RootCause, CodeFix? Fix) ParseSingleExceptionResponse(string content)
    {
        try
        {
            var json = ExtractJson(content);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var action = root.TryGetProperty("action", out var actionProp)
                ? actionProp.GetString() ?? "fix"
                : "fix";

            var rootCause = root.TryGetProperty("rootCause", out var rcProp)
                ? rcProp.GetString()
                : null;

            CodeFix? fix = null;
            if (root.TryGetProperty("fix", out var fixProp) && fixProp.ValueKind == JsonValueKind.Object)
            {
                fix = new CodeFix
                {
                    FilePath = fixProp.TryGetProperty("filePath", out var fp) ? fp.GetString() ?? "" : "",
                    OriginalCode = fixProp.TryGetProperty("originalCode", out var oc) ? oc.GetString() ?? "" : "",
                    FixedCode = fixProp.TryGetProperty("fixedCode", out var fc) ? fc.GetString() ?? "" : "",
                    Explanation = fixProp.TryGetProperty("explanation", out var ex) ? ex.GetString() ?? "" : "",
                    Confidence = fixProp.TryGetProperty("confidence", out var cf) ? cf.GetString() ?? "Medium" : "Medium",
                    Severity = root.TryGetProperty("severity", out var sv) ? sv.GetString() ?? "Medium" : "Medium",
                    RelatedExceptions = []
                };
            }

            return (action, rootCause, fix);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse single exception response");
            return ("error", "Failed to parse response", null);
        }
    }

    /// <summary>
    /// Creates a PR for a single exception fix
    /// </summary>
    private async Task<string?> CreateSingleExceptionPrAsync(
        ExceptionInfo exception,
        CodeFix fix,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(fix.FilePath) || string.IsNullOrEmpty(fix.FixedCode))
        {
            logger.LogWarning("Invalid fix for {Type}: missing filePath or fixedCode", exception.ExceptionType);
            return null;
        }

        try
        {
            var json = await _gitHubPlugin!.GetFileContentAsync(fix.FilePath);
            string currentContent;

            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("error", out var errorProp))
                {
                    logger.LogWarning("Could not fetch file {Path}: {Error}", fix.FilePath, errorProp.GetString());
                    return null;
                }
                if (!doc.RootElement.TryGetProperty("content", out var contentProp))
                {
                    logger.LogWarning("Invalid GitHub response for {Path}", fix.FilePath);
                    return null;
                }
                currentContent = contentProp.GetString() ?? "";
            }

            var newContent = currentContent;
            var isPlaceholder = fix.OriginalCode?.Contains("// Unable") == true ||
                fix.OriginalCode?.Contains("// Line") == true ||
                fix.OriginalCode?.StartsWith("//") == true;

            if (isPlaceholder && exception.LineNumber.HasValue)
            {
                List<string> lines = [.. currentContent.Split('\n')];
                var lineNum = exception.LineNumber.Value;

                if (lineNum > 0 && lineNum <= lines.Count)
                {
                    var buggyLine = lines[lineNum - 1];
                    var indexMatch = System.Text.RegularExpressions.Regex.Match(buggyLine, @"(\w+)\s*\[");

                    if (indexMatch.Success)
                    {
                        var varName = indexMatch.Groups[1].Value;
                        var indent = new string(' ', buggyLine.TakeWhile(char.IsWhiteSpace).Count());
                        var checkLine = $"{indent}if ({varName}.Count == 0) {{ logger.LogWarning(\"No items in {varName}\"); return Ok(new {{ message = \"No results found\" }}); }}";
                        lines.Insert(lineNum - 1, checkLine);
                        newContent = string.Join('\n', lines);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(fix.OriginalCode))
            {
                if (currentContent.Contains(fix.OriginalCode))
                {
                    newContent = currentContent.Replace(fix.OriginalCode, fix.FixedCode);
                }
                else
                {
                    logger.LogWarning("Original code not found in {Path}", fix.FilePath);
                    return null;
                }
            }

            if (newContent == currentContent)
            {
                logger.LogWarning("No changes applied to {Path}", fix.FilePath);
                return null;
            }

            // Generate PR title and description
            var exceptionName = exception.ExceptionType.Split('.').Last();
            var controllerName = "";
            if (!string.IsNullOrEmpty(exception.SourceFile))
            {
                var fileName = Path.GetFileNameWithoutExtension(exception.SourceFile);
                if (fileName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
                    controllerName = fileName;
            }

            var title = !string.IsNullOrEmpty(controllerName)
                ? $"fix({controllerName}): Handle {exceptionName}"
                : $"fix: Handle {exceptionName}";

            var appInsightsLink = BuildAppInsightsLink(exception);
            var appInsightsSection = !string.IsNullOrEmpty(appInsightsLink)
                ? $"- **App Insights**: [View Exception]({appInsightsLink})"
                : "";

            var description = $"""
                ## Summary
                {fix.Explanation}

                ## Exception Details
                - **Type**: `{exception.ExceptionType}`
                - **Occurrences**: {exception.OccurrenceCount}
                - **Source**: `{exception.SourceFile}:{exception.LineNumber}`
                - **Operation**: {exception.OperationName}
                {(!string.IsNullOrEmpty(exception.ProblemId) ? $"- **ProblemId**: `{exception.ProblemId}`" : "")}
                {appInsightsSection}

                ## Root Cause
                {fix.Explanation.Split('.')[0]}

                ---
                *Generated by AI Diagnostics Agent*
                """;

            var fileChanges = JsonSerializer.Serialize(new[] { new { Path = fix.FilePath, Content = newContent } });
            var result = await _gitHubPlugin.CreatePullRequestAsync(title, description, fileChanges);

            // Extract PR URL
            string? prUrl = null;
            try
            {
                using var resultDoc = JsonDocument.Parse(result);
                if (resultDoc.RootElement.TryGetProperty("pullRequestUrl", out var urlProp))
                    prUrl = urlProp.GetString();
                else if (resultDoc.RootElement.TryGetProperty("html_url", out var htmlProp))
                    prUrl = htmlProp.GetString();
            }
            catch { }

            if (string.IsNullOrEmpty(prUrl))
            {
                var urlMatch = System.Text.RegularExpressions.Regex.Match(result ?? "", @"https://github\.com/[^\s""]+/pull/\d+");
                if (urlMatch.Success)
                    prUrl = urlMatch.Value;
            }

            if (!string.IsNullOrEmpty(prUrl))
            {
                logger.LogInformation("Created PR for {Type}: {Url}", exception.ExceptionType, prUrl);
            }

            return prUrl;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create PR for {Type}", exception.ExceptionType);
            return null;
        }
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
                        ? ln.GetInt32() : null,
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
    /// Hybrid analysis: Pre-fetches data but allows Claude to use tools if needed.
    /// Best of both worlds - fast when data is sufficient, flexible when more context is needed.
    /// </summary>
    public async Task<SingleExceptionResult> AnalyzeHybridAsync(
        ExceptionInfo exception,
        bool createPr,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting hybrid analysis for {Type}", exception.ExceptionType);

        GetOrCreateAgent();

        // pre-fetch source file
        string? fileContent = null;
        if (!string.IsNullOrEmpty(exception.SourceFile) && _gitHubPlugin != null)
        {
            try
            {
                var json = await _gitHubPlugin.GetFileContentAsync(exception.SourceFile);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("content", out var contentProp))
                {
                    fileContent = contentProp.GetString();
                    if (fileContent?.Length > 15000)
                        fileContent = fileContent[..15000] + "\n... (truncated)";
                }
                logger.LogInformation("Pre-fetched source file: {File} ({Length} chars)",
                    exception.SourceFile, fileContent?.Length ?? 0);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to pre-fetch source file: {File}", exception.SourceFile);
            }
        }

        var contextBuilder = new System.Text.StringBuilder();
        contextBuilder.AppendLine("# Exception to Analyze");
        contextBuilder.AppendLine();
        contextBuilder.AppendLine($"**Type**: {exception.ExceptionType}");
        contextBuilder.AppendLine($"**Message**: {exception.Message}");
        contextBuilder.AppendLine($"**Occurrences**: {exception.OccurrenceCount}");
        contextBuilder.AppendLine($"**Operation**: {exception.OperationName}");
        contextBuilder.AppendLine($"**Source File**: {exception.SourceFile ?? "Unknown"}");
        contextBuilder.AppendLine($"**Line**: {exception.LineNumber?.ToString() ?? "Unknown"}");
        contextBuilder.AppendLine();
        contextBuilder.AppendLine("**Stack Trace**:");
        contextBuilder.AppendLine("```");
        contextBuilder.AppendLine(exception.StackTrace);
        contextBuilder.AppendLine("```");

        if (!string.IsNullOrEmpty(fileContent))
        {
            contextBuilder.AppendLine();
            contextBuilder.AppendLine($"## Source Code (Pre-fetched): {exception.SourceFile}");
            contextBuilder.AppendLine("```csharp");
            var lines = fileContent.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                contextBuilder.AppendLine($"{i + 1,4}: {lines[i]}");
            }
            contextBuilder.AppendLine("```");
        }

        // HYBRID PROMPT: Tells Claude to use tools ONLY if needed
        var prompt = $$"""
            You are an expert .NET developer. Analyze this production exception.

            {{contextBuilder}}

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

        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage(prompt);

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        var response = await chatService.GetChatMessageContentsAsync(
            chatHistory,
            kernel: _kernel,
            cancellationToken: cancellationToken);

        var fullResponse = string.Join("", response.Select(r => r.Content));
        logger.LogDebug("Hybrid analysis response for {Type}: {Response}",
            exception.ExceptionType, fullResponse.Length > 500 ? fullResponse[..500] + "..." : fullResponse);

        var parsed = ParseSingleExceptionResponse(fullResponse);

        if (parsed.Action == "no_fix_needed")
        {
            logger.LogInformation("Exception {Type} marked as no_fix_needed: {Reason}",
                exception.ExceptionType, parsed.RootCause);
            return new SingleExceptionResult("no_fix_needed", parsed.RootCause, null, null);
        }

        if (parsed.Fix is null)
        {
            logger.LogWarning("No fix provided for {Type}", exception.ExceptionType);
            return new SingleExceptionResult("error", "No fix provided by Claude", null, null);
        }

        // Create PR if requested
        string? prUrl = null;
        if (createPr && _gitHubPlugin != null)
        {
            prUrl = await CreateSingleExceptionPrAsync(exception, parsed.Fix, cancellationToken);
        }

        return new SingleExceptionResult("fix", parsed.RootCause, parsed.Fix, prUrl);
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
        List<string> responses = [];
        await foreach (var response in agent.InvokeAsync(chatHistory, cancellationToken: cancellationToken))
        {
            responses.Add(response.Message?.Content ?? response.ToString() ?? "");
        }
#pragma warning restore SKEXP0110

        return ParseCodeFix(string.Join("", responses), exception);
    }

    /// <summary>
    /// Builds an App Insights portal deep link for the exception
    /// </summary>
    private string? BuildAppInsightsLink(ExceptionInfo exception)
    {
        if (string.IsNullOrEmpty(exception.ItemId) || string.IsNullOrEmpty(exception.ItemTimestamp))
            return null;

        var appInsights = _config.AppInsights;
        if (string.IsNullOrEmpty(appInsights.ResourceName) ||
            string.IsNullOrEmpty(appInsights.ResourceGroup) ||
            string.IsNullOrEmpty(appInsights.SubscriptionId))
        {
            return null;
        }

        // Build the Azure Portal deep link
        // Format: https://portal.azure.com/#blade/AppInsightsExtension/DetailsV2Blade/DataModel/{dataModel}/ComponentId/{componentId}
        var dataModel = Uri.EscapeDataString(JsonSerializer.Serialize(new
        {
            eventId = exception.ItemId,
            timestamp = exception.ItemTimestamp
        }));

        var componentId = Uri.EscapeDataString(JsonSerializer.Serialize(new
        {
            Name = appInsights.ResourceName,
            ResourceGroup = appInsights.ResourceGroup,
            SubscriptionId = appInsights.SubscriptionId
        }));

        return $"https://portal.azure.com/#blade/AppInsightsExtension/DetailsV2Blade/DataModel/{dataModel}/ComponentId/{componentId}";
    }

    #region Parsing Helpers

    /// <summary>
    /// Parses the report and returns skipped validation errors separately.
    /// </summary>
    private (DiagnosticReport Report, List<string> SkippedValidationErrors) ParseReportWithValidationCheck(string content)
    {
        var skippedValidationErrors = new List<string>();

        try
        {
            var json = ExtractJson(content);
            var parsed = JsonSerializer.Deserialize<ReportDto>(json, JsonOptions);

            // Track exceptions marked as no_fix_needed (input validation errors)
            if (parsed?.Exceptions is not null)
            {
                foreach (var ex in parsed.Exceptions)
                {
                    if (string.Equals(ex.Action, "no_fix_needed", StringComparison.OrdinalIgnoreCase))
                    {
                        skippedValidationErrors.Add($"{ex.Type}: {ex.RootCause}");
                    }
                }
            }

            var report = new DiagnosticReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                AnalysisPeriod = TimeSpan.FromHours(24),
                Summary = parsed?.Summary ?? "Analysis completed",
                PullRequestUrl = parsed?.PullRequestUrl,
                Exceptions = parsed?.Exceptions is { } exceptions
                    ? [.. exceptions.Select(e => new ExceptionInfo
                    {
                        ExceptionType = e.Type ?? "Unknown",
                        Message = e.RootCause ?? "",
                        StackTrace = "",
                        OperationName = "",
                        Timestamp = DateTimeOffset.UtcNow,
                        OccurrenceCount = e.Occurrences
                    })]
                    : [],
                ProposedFixes = parsed?.Fixes is { } fixes
                    ? [.. fixes.Select(f => new CodeFix
                    {
                        FilePath = f.FilePath ?? "",
                        OriginalCode = f.OriginalCode ?? "",
                        FixedCode = f.FixedCode ?? "",
                        Explanation = f.Explanation ?? "",
                        Severity = f.Severity ?? "Medium",
                        Confidence = f.Confidence ?? "Medium",
                        RelatedExceptions = []
                    })]
                    : []
            };

            return (report, skippedValidationErrors);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse report JSON, returning empty report");
            var emptyReport = new DiagnosticReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                AnalysisPeriod = TimeSpan.FromHours(24),
                Summary = content.Length > 500 ? content[..500] : content,
                Exceptions = [],
                ProposedFixes = []
            };
            return (emptyReport, skippedValidationErrors);
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
        string? RootCause,
        string? Action);  // "fix" | "no_fix_needed" | "investigate"

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

        if (filter.ExcludedTypes.Any(t => exceptionType.Equals(t, StringComparison.OrdinalIgnoreCase)))
        {
            return new ExceptionFilterResult(IsFiltered: true, IsAmbiguous: false, Reason: "Excluded exception type");
        }

        var matchedPattern = filter.ExcludedPatterns.FirstOrDefault(p =>
            exceptionType.Contains(p, StringComparison.OrdinalIgnoreCase) ||
            message.Contains(p, StringComparison.OrdinalIgnoreCase));

        if (matchedPattern != null)
        {
            return new ExceptionFilterResult(IsFiltered: true, IsAmbiguous: false, Reason: $"Matched pattern: {matchedPattern}");
        }

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
            return true;
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
