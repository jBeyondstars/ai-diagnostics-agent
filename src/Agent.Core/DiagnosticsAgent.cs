using System.Text.Json;
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

namespace Agent.Core;

public sealed class DiagnosticsAgent : IDisposable
{
    private readonly AgentConfiguration _config;
    private readonly ILogger<DiagnosticsAgent> _logger;
    private readonly DeduplicationService? _deduplicationService;
    private readonly Kernel _kernel;
    private readonly CodeAnalyzer _analyzer;
    private readonly ExceptionsParser _parser;

    private AppInsightsPlugin? _appInsightsPlugin;
    private GitHubPlugin? _gitHubPlugin;
    private ChatCompletionAgent? _agent;
    private bool _disposed;

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

    public DiagnosticsAgent(
        IOptions<AgentConfiguration> config,
        ILogger<DiagnosticsAgent> logger,
        ILoggerFactory loggerFactory,
        DeduplicationService? deduplicationService = null)
    {
        _config = config.Value;
        _logger = logger;
        _deduplicationService = deduplicationService;
        
        _kernel = CreateKernel(_config, logger);
        
        _analyzer = new CodeAnalyzer(_kernel, loggerFactory.CreateLogger<CodeAnalyzer>());
        _parser = new ExceptionsParser(loggerFactory.CreateLogger<ExceptionsParser>());
    }

    public async Task<DiagnosticReport> AnalyzeAndFixAsync(AnalysisRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Starting per-exception analysis: {Hours}h lookback, CreatePR={CreatePR}, MaxExceptions={Max}",
            request.HoursToAnalyze,
            request.CreatePullRequest,
            request.MaxExceptionsToAnalyze);

        InitializePlugins();

        // PHASE 1: Get and filter exceptions
        _logger.LogInformation("Phase 1: Fetching exceptions from AppInsights...");

        string exceptionsJson;
        if (request.LatestOnly)
        {
            _logger.LogInformation("LatestOnly mode: fetching only the most recent exception");
            exceptionsJson = await _appInsightsPlugin!.GetLatestExceptionAsync(request.HoursToAnalyze);
        }
        else
        {
            exceptionsJson = await _appInsightsPlugin!.GetExceptionsAsync(
                request.HoursToAnalyze,
                request.MinOccurrences,
                request.MaxExceptionsToAnalyze);
        }

        var allExceptions = _parser.Parse(exceptionsJson);
        _logger.LogInformation("Found {Count} exceptions to analyze", allExceptions.Count);

        if (allExceptions.Count == 0)
        {
            return EmptyReport(request, "No exceptions found matching the criteria.");
        }

        var actionableExceptions = await FilterExceptionsAsync(allExceptions, ct);
        if (actionableExceptions.Count == 0)
        {
            return EmptyReport(
                request,
                $"Found {allExceptions.Count} exceptions but all were infrastructure/transient errors.",
                allExceptions);
        }

        // PHASE 2: Process each exception individually
        _logger.LogInformation("Phase 2: Processing {Count} exceptions individually...", actionableExceptions.Count);

        var fixes = new List<CodeFix>();
        var prUrls = new List<string>();
        var skippedValidation = new List<string>();
        var skippedExistingPr = new List<string>();
        var skippedDedup = new List<string>();

        foreach (var ex in actionableExceptions)
        {
            _logger.LogInformation("Processing exception: {Type} ({Count} occurrences)",
                ex.ExceptionType, ex.OccurrenceCount);

            // Check for existing PR
            if (_gitHubPlugin != null)
            {
                var existingPr = await _gitHubPlugin.CheckExistingPrAsync(ex.ExceptionType, ex.ProblemId, ex.SourceFile);
                if (existingPr.Exists)
                {
                    _logger.LogInformation("PR already exists for {Type}: {Url}", ex.ExceptionType, existingPr.PrUrl);
                    skippedExistingPr.Add($"{ex.ExceptionType} → {existingPr.PrUrl}");
                    continue;
                }
            }

            // Check deduplication
            if (_deduplicationService != null)
            {
                var shouldAnalyze = await _deduplicationService.ShouldAnalyzeAsync(ex.ProblemId ?? ex.ExceptionType, ct);
                if (!shouldAnalyze)
                {
                    _logger.LogInformation("Skipping {Type} - recently analyzed", ex.ExceptionType);
                    skippedDedup.Add(ex.ExceptionType);
                    continue;
                }
            }

            var result = await AnalyzeSingleException(ex, request.CreatePullRequest, isHybrid: false, ct);

            if (result.Action == "no_fix_needed")
            {
                skippedValidation.Add($"{ex.ExceptionType}: {result.RootCause}");
                continue;
            }

            if (result.Fix is not null) fixes.Add(result.Fix);
            if (result.PrUrl is not null)
            {
                prUrls.Add(result.PrUrl);
                _logger.LogInformation("Created PR for {Type}: {Url}", ex.ExceptionType, result.PrUrl);
            }
        }

        // Build summary
        var summaryParts = new List<string> { $"Analyzed {actionableExceptions.Count} exceptions" };
        if (prUrls.Count > 0) summaryParts.Add($"{prUrls.Count} PRs created");
        if (skippedValidation.Count > 0) summaryParts.Add($"{skippedValidation.Count} input validation errors (no fix needed)");
        if (skippedExistingPr.Count > 0) summaryParts.Add($"{skippedExistingPr.Count} already have open PRs");
        if (skippedDedup.Count > 0) summaryParts.Add($"{skippedDedup.Count} recently analyzed");

        if (skippedValidation.Count > 0)
        {
            _logger.LogInformation("Input validation errors skipped: {Types}", string.Join("; ", skippedValidation));
        }
        if (skippedExistingPr.Count > 0)
        {
            _logger.LogInformation("Exceptions with existing PRs: {Types}", string.Join("; ", skippedExistingPr));
        }

        _logger.LogInformation("Analysis complete: {Summary}", string.Join(", ", summaryParts));

        return new DiagnosticReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            AnalysisPeriod = TimeSpan.FromHours(request.HoursToAnalyze),
            Summary = string.Join(". ", summaryParts) + ".",
            Exceptions = actionableExceptions,
            ProposedFixes = fixes,
            PullRequestUrl = prUrls.Count == 1 ? prUrls[0] : null,
            PullRequestUrls = prUrls
        };
    }
    
    public async Task<DiagnosticReport> AnalyzeBatchHybridAsync(AnalysisRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Starting HYBRID batch analysis: {Hours}h lookback, CreatePR={CreatePR}, MaxExceptions={Max}",
            request.HoursToAnalyze,
            request.CreatePullRequest,
            request.MaxExceptionsToAnalyze);

        InitializePlugins();

        var exceptionsJson = await _appInsightsPlugin!.GetExceptionsAsync(
            request.HoursToAnalyze,
            request.MinOccurrences,
            request.MaxExceptionsToAnalyze);

        var exceptions = _parser.Parse(exceptionsJson);
        _logger.LogInformation("Found {Count} exceptions for hybrid analysis", exceptions.Count);

        var fixes = new List<CodeFix>();
        var prUrls = new List<string>();

        foreach (var ex in exceptions)
        {
            _logger.LogInformation("Hybrid analyzing: {Type} ({Count} occurrences)", ex.ExceptionType, ex.OccurrenceCount);

            try
            {
                var result = await AnalyzeSingleException(ex, request.CreatePullRequest, isHybrid: true, ct);
                if (result.Fix is not null) fixes.Add(result.Fix);
                if (result.PrUrl is not null)
                {
                    prUrls.Add(result.PrUrl);
                    _logger.LogInformation("Created PR for {Type}: {Url}", ex.ExceptionType, result.PrUrl);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error during hybrid analysis of {Type}", ex.ExceptionType);
            }
        }

        _logger.LogInformation("Hybrid analysis complete: {Fixes} fixes proposed, {PRs} PRs created", fixes.Count, prUrls.Count);
        return CreateReport(request, exceptions, fixes, prUrls);
    }

    public async Task<SingleExceptionResult> AnalyzeHybridAsync(ExceptionInfo exception, bool createPr, CancellationToken ct)
    {
        InitializePlugins();
        return await AnalyzeSingleException(exception, createPr, isHybrid: true, ct);
    }

    public async IAsyncEnumerable<string> AnalyzeStreamingAsync(AnalysisRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        InitializePlugins();
        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage($"Analyze exceptions from last {request.HoursToAnalyze} hours.");
        
        if (_agent is null) InitializeAgent();

#pragma warning disable SKEXP0110
        await foreach (var response in _agent!.InvokeStreamingAsync(chatHistory, cancellationToken: ct))
        {
            var content = response.Message?.Content ?? response.ToString();
            if (!string.IsNullOrEmpty(content)) yield return content;
        }
#pragma warning restore SKEXP0110
    }

    private async Task<SingleExceptionResult> AnalyzeSingleException(ExceptionInfo ex, bool createPr, bool isHybrid, CancellationToken ct)
    {
        string? fileContent = null;
        if (!string.IsNullOrEmpty(ex.SourceFile) && _gitHubPlugin != null)
        {
            try
            {
                var json = await _gitHubPlugin.GetFileContentAsync(ex.SourceFile);
                fileContent = ExtractContentFromJson(json);
                if (fileContent?.Length > 15000) fileContent = fileContent[..15000] + "\n... (truncated)";
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to fetch source file: {File}", ex.SourceFile);
            }
        }

        var result = await _analyzer.AnalyzeAsync(ex, fileContent, allowTools: isHybrid, ct);

        if (result.Action == "no_fix_needed")
        {
            _logger.LogInformation("Exception {Type} marked as no_fix_needed: {Reason}", ex.ExceptionType, result.RootCause);
            return result;
        }

        if (result.Fix is null)
        {
            _logger.LogWarning("No fix provided for {Type}", ex.ExceptionType);
            return result;
        }

        if (createPr && _gitHubPlugin != null)
        {
            var prUrl = await CreatePrAsync(ex, result.Fix, ct);
            result = result with { PrUrl = prUrl };

            if (prUrl != null && _deduplicationService != null)
            {
                var pid = ex.ProblemId ?? ex.ExceptionType;
                await _deduplicationService.MarkAsAnalyzedAsync(pid, TimeSpan.FromHours(24), ct);
                await _deduplicationService.AddToIndexAsync(ex.ExceptionType, pid, ct);
            }
        }

        return result;
    }

    private async Task<string?> CreatePrAsync(ExceptionInfo ex, CodeFix fix, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fix.FilePath) || string.IsNullOrEmpty(fix.FixedCode)) return null;

        try
        {
            var json = await _gitHubPlugin!.GetFileContentAsync(fix.FilePath);
            var currentContent = ExtractContentFromJson(json);
            if (currentContent is null) return null;

            var newContent = ApplyFix(currentContent, fix);
            if (newContent == currentContent)
            {
                _logger.LogWarning("Fix application resulted in no changes for {File}", fix.FilePath);
                return null;
            }

            // Generate PR title
            var exceptionName = ex.ExceptionType.Split('.').Last();
            var controllerName = "";
            if (!string.IsNullOrEmpty(ex.SourceFile))
            {
                var fileName = Path.GetFileNameWithoutExtension(ex.SourceFile);
                if (fileName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
                    controllerName = fileName;
            }

            var title = !string.IsNullOrEmpty(controllerName)
                ? $"fix({controllerName}): Handle {exceptionName}"
                : $"fix: Handle {exceptionName}";

            var description = BuildPrDescription(ex, fix);
            var fileChanges = JsonSerializer.Serialize(new[] { new { Path = fix.FilePath, Content = newContent } });

            var prResult = await _gitHubPlugin.CreatePullRequestAsync(title, description, fileChanges);
            return ExtractPrUrl(prResult);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to create PR for {Type}", ex.ExceptionType);
            return null;
        }
    }

    private string ApplyFix(string content, CodeFix fix)
    {
        if (!string.IsNullOrEmpty(fix.OriginalCode) && content.Contains(fix.OriginalCode))
        {
            return content.Replace(fix.OriginalCode, fix.FixedCode);
        }
        return content;
    }

    private static string? ExtractContentFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
    }

    private static string? ExtractPrUrl(string json)
    {
        try 
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("pullRequestUrl", out var url)) return url.GetString();
            if (doc.RootElement.TryGetProperty("html_url", out var html)) return html.GetString();
        } catch {{ }} 
        return null; 
    }

    private string BuildPrDescription(ExceptionInfo ex, CodeFix fix)
    {
        var appInsightsLink = BuildAppInsightsLink(ex);
        var appInsightsSection = !string.IsNullOrEmpty(appInsightsLink)
            ? $"- **App Insights**: [View Exception]({appInsightsLink})"
            : "";

        return $"""
            ## Summary
            {fix.Explanation}

            ## Exception Details
            - **Type**: `{ex.ExceptionType}`
            - **Occurrences**: {ex.OccurrenceCount}
            - **Source**: `{ex.SourceFile}:{ex.LineNumber}`
            - **Operation**: {ex.OperationName}
            {(!string.IsNullOrEmpty(ex.ProblemId) ? $"- **ProblemId**: `{ex.ProblemId}`" : "")}
            {appInsightsSection}

            ## Root Cause
            {fix.Explanation.Split('.')[0]}

            ---
            *Generated by AI Diagnostics Agent*
            """;
    }

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

    private async Task<List<ExceptionInfo>> FilterExceptionsAsync(List<ExceptionInfo> exceptions, CancellationToken ct)
    {
        var result = new List<ExceptionInfo>();
        var filter = _config.ExceptionFilter;

        foreach (var ex in exceptions)
        {
            var filterResult = IsNonActionableException(ex, filter);

            if (filterResult.IsFiltered)
            {
                _logger.LogInformation("Skipping non-actionable exception {Type}: {Reason}", ex.ExceptionType, filterResult.Reason);
                continue;
            }

            if (filterResult.IsAmbiguous && filter.EnableClaudeEvaluation)
            {
                var isFixable = await EvaluateIfFixableAsync(ex, ct);
                if (!isFixable)
                {
                    _logger.LogInformation("Claude determined {Type} is not fixable by code changes", ex.ExceptionType);
                    continue;
                }
            }

            result.Add(ex);
        }

        _logger.LogInformation("After filtering: {Actionable}/{Total} actionable exceptions", result.Count, exceptions.Count);
        return result;
    }

    private readonly record struct ExceptionFilterResult(bool IsFiltered, bool IsAmbiguous, string? Reason);

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

    private async Task<bool> EvaluateIfFixableAsync(ExceptionInfo exception, CancellationToken ct)
    {
        try
        {
            var chatService = _kernel.GetRequiredService<IChatCompletionService>();
            var chatHistory = new ChatHistory();

            var prompt = $"""
                You are evaluating if a production exception can be fixed by changing the application code.

                Exception Type: {exception.ExceptionType}
                Message: {exception.Message}
                Stack Trace (first 500 chars): {(exception.StackTrace?.Length > 500 ? exception.StackTrace[..500] : exception.StackTrace ?? "N/A")}
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

            var response = await chatService.GetChatMessageContentsAsync(chatHistory, kernel: null, cancellationToken: ct);
            var answer = string.Join("", response.Select(r => r.Content)).Trim().ToUpperInvariant();
            _logger.LogDebug("Claude evaluation for {Type}: {Answer}", exception.ExceptionType, answer);

            return answer.Contains("YES");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to evaluate exception fixability, defaulting to true");
            return true;
        }
    }

    private void InitializePlugins()
    {
        if (_appInsightsPlugin != null) return;

        _appInsightsPlugin = new AppInsightsPlugin(_config.AppInsights.WorkspaceId, _logger as ILogger<AppInsightsPlugin>);
        _kernel.ImportPluginFromObject(_appInsightsPlugin, "AppInsights");

        _gitHubPlugin = new GitHubPlugin(_config.GitHub.Token, _config.GitHub.Owner, _config.GitHub.Repo, _config.GitHub.DefaultBranch, _logger as ILogger<GitHubPlugin>);
        _kernel.ImportPluginFromObject(_gitHubPlugin, "GitHub");

        _logger.LogInformation("Registered plugins: AppInsights, GitHub ({Owner}/{Repo})", _config.GitHub.Owner, _config.GitHub.Repo);
    }
    
    private void InitializeAgent()
    {
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
    }

    private static Kernel CreateKernel(AgentConfiguration config, ILogger logger)
    {
        var builder = Kernel.CreateBuilder();

        switch (config.Llm.Provider.ToLowerInvariant())
        {
            case "anthropic":
            case "claude":
                logger.LogInformation("Using Anthropic Claude with model {Model}", config.Llm.Anthropic.Model);
                builder.Services.AddSingleton<IChatCompletionService>(new AnthropicChatCompletionService(
                    config.Llm.Anthropic.ApiKey,
                    config.Llm.Anthropic.Model,
                    logger));
                break;

            case "openai":
                logger.LogInformation("Using OpenAI provider with model {Model}", config.Llm.OpenAI.Model);
                builder.AddOpenAIChatCompletion(config.Llm.OpenAI.Model, config.Llm.OpenAI.ApiKey);
                break;

            case "azureopenai":
                logger.LogInformation("Using Azure OpenAI provider with deployment {Deployment}", config.Llm.AzureOpenAI.DeploymentName);
                builder.AddAzureOpenAIChatCompletion(
                    config.Llm.AzureOpenAI.DeploymentName,
                    config.Llm.AzureOpenAI.Endpoint,
                    config.Llm.AzureOpenAI.ApiKey);
                break;

            default:
                throw new ArgumentException($"Unknown LLM provider: {config.Llm.Provider}. Supported: Anthropic, Claude, OpenAI, AzureOpenAI");
        }

        return builder.Build();
    }

    private static DiagnosticReport CreateReport(AnalysisRequest req, List<ExceptionInfo> exceptions, List<CodeFix> fixes, List<string> prUrls) => new()
    {
        GeneratedAt = DateTimeOffset.UtcNow,
        AnalysisPeriod = TimeSpan.FromHours(req.HoursToAnalyze),
        Summary = $"Analyzed {exceptions.Count} exceptions, proposed {fixes.Count} fixes, created {prUrls.Count} PRs.",
        Exceptions = exceptions,
        ProposedFixes = fixes,
        PullRequestUrls = prUrls,
        PullRequestUrl = prUrls.Count == 1 ? prUrls[0] : null
    };
    
    private static DiagnosticReport EmptyReport(AnalysisRequest req, string summary, List<ExceptionInfo>? exceptions = null) => new()
    {
        GeneratedAt = DateTimeOffset.UtcNow,
        AnalysisPeriod = TimeSpan.FromHours(req.HoursToAnalyze),
        Summary = summary,
        Exceptions = exceptions ?? [],
        ProposedFixes = []
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}