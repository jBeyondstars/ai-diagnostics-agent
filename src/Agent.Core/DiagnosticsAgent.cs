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

/// <summary>
/// The main Diagnostics Agent that orchestrates exception analysis and code fixing.
/// Uses Semantic Kernel Agents Framework GA (1.45+) with modern C# 14 patterns.
/// </summary>
public sealed class DiagnosticsAgent(
    IOptions<AgentConfiguration> config,
    ILogger<DiagnosticsAgent> logger) : IDisposable
{
    private readonly AgentConfiguration _config = config.Value;
    private readonly Kernel _kernel = CreateKernel(config.Value, logger);
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

        // Register plugins
        var appInsightsPlugin = new AppInsightsPlugin(
            _config.AppInsights.WorkspaceId,
            logger as ILogger<AppInsightsPlugin>);
        _kernel.ImportPluginFromObject(appInsightsPlugin, "AppInsights");

        var gitHubPlugin = new GitHubPlugin(
            _config.GitHub.Token,
            _config.GitHub.Owner,
            _config.GitHub.Repo,
            _config.GitHub.DefaultBranch,
            logger as ILogger<GitHubPlugin>);
        _kernel.ImportPluginFromObject(gitHubPlugin, "GitHub");

        logger.LogInformation("Registered plugins: AppInsights, GitHub ({Owner}/{Repo})",
            _config.GitHub.Owner, _config.GitHub.Repo);

        // Create the agent using Semantic Kernel Agents Framework GA
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
    /// Runs a full analysis and optionally creates PRs/Issues
    /// </summary>
    public async Task<DiagnosticReport> AnalyzeAndFixAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Starting analysis: {Hours}h lookback, CreatePR={CreatePR}, MaxExceptions={Max}",
            request.HoursToAnalyze,
            request.CreatePullRequest,
            request.MaxExceptionsToAnalyze);

        var agent = GetOrCreateAgent();

        #pragma warning disable SKEXP0110
        var chatHistory = new ChatHistory();
        #pragma warning restore SKEXP0110

        var userPrompt = $$"""
            Analyze the exceptions from the last {{request.HoursToAnalyze}} hours.

            Requirements:
            - Minimum {{request.MinOccurrences}} occurrences to consider
            - Analyze up to {{request.MaxExceptionsToAnalyze}} most critical exceptions
            - For each critical exception, retrieve and analyze the source code
            - Propose concrete fixes with before/after code

            After analysis, respond with a JSON report in this exact format:
            ```json
            {
                "summary": "Brief executive summary",
                "exceptionsAnalyzed": 5,
                "fixesProposed": 3,
                "exceptions": [
                    {
                        "type": "System.NullReferenceException",
                        "occurrences": 150,
                        "severity": "Critical",
                        "rootCause": "Description of root cause"
                    }
                ],
                "fixes": [
                    {
                        "filePath": "src/Services/OrderService.cs",
                        "originalCode": "var name = customer.Name;",
                        "fixedCode": "var name = customer?.Name ?? string.Empty;",
                        "explanation": "Added null-conditional operator to prevent NRE",
                        "severity": "Critical",
                        "confidence": "High"
                    }
                ]
            }
            ```

            Start by calling get_exceptions to discover what's happening.
            """;

        chatHistory.AddUserMessage(userPrompt);

        logger.LogInformation("Agent starting analysis...");

        // Invoke the agent
        #pragma warning disable SKEXP0110
        var responses = new List<string>();
        await foreach (var response in agent.InvokeAsync(chatHistory, cancellationToken: cancellationToken))
        {
            var content = response.Message?.Content ?? response.ToString();
            responses.Add(content ?? "");
            logger.LogDebug("Agent response chunk received");
        }
        #pragma warning restore SKEXP0110

        var fullResponse = string.Join("", responses);
        logger.LogDebug("Agent response: {Response}", fullResponse);

        var report = ParseReport(fullResponse);

        // Step 2: Create PR if requested and we have fixes
        if (request.CreatePullRequest && report.ProposedFixes.Count > 0)
        {
            logger.LogInformation("Creating Pull Request with {Count} fixes", report.ProposedFixes.Count);

            chatHistory.AddAssistantMessage(fullResponse);
            chatHistory.AddUserMessage("""
                Great analysis! Now create a Pull Request with the fixes you proposed.

                Use the create_pull_request function with:
                - A clear, descriptive title
                - A detailed description explaining each fix
                - The file changes in JSON format

                Make sure the PR description includes:
                1. Summary of exceptions fixed
                2. Root cause analysis
                3. How the fixes address each issue
                """);

            #pragma warning disable SKEXP0110
            var prResponses = new List<string>();
            await foreach (var response in agent.InvokeAsync(chatHistory, cancellationToken: cancellationToken))
            {
                var content = response.Message?.Content ?? response.ToString();
                prResponses.Add(content ?? "");
            }
            #pragma warning restore SKEXP0110

            var prUrl = ExtractUrl(string.Join("", prResponses), "github.com");
            if (prUrl is not null)
            {
                report = report with { PullRequestUrl = prUrl };
                logger.LogInformation("Created PR: {Url}", prUrl);
            }
        }

        // Step 3: Create Issues for problems we couldn't fix
        if (request.CreateIssues)
        {
            var unfixedExceptions = report.Exceptions
                .Where(e => !report.ProposedFixes.Any(f => f.RelatedExceptions.Contains(e.ExceptionType)))
                .Where(e => e.Severity is "Critical" or "High")
                .ToList();

            if (unfixedExceptions.Count > 0)
            {
                logger.LogInformation("Creating Issues for {Count} unfixed exceptions", unfixedExceptions.Count);

                chatHistory.AddUserMessage($$"""
                    Some critical exceptions couldn't be automatically fixed.
                    Create GitHub Issues for these {{unfixedExceptions.Count}} exceptions
                    so the team can investigate manually.

                    Include in each issue:
                    - Exception details and frequency
                    - Stack trace summary
                    - Your analysis of potential causes
                    - Suggested investigation steps
                    """);

                #pragma warning disable SKEXP0110
                await foreach (var _ in agent.InvokeAsync(chatHistory, cancellationToken: cancellationToken))
                {
                    // Process issue creation responses
                }
                #pragma warning restore SKEXP0110
            }
        }

        logger.LogInformation(
            "Analysis complete: {Exceptions} exceptions, {Fixes} fixes proposed",
            report.Exceptions.Count,
            report.ProposedFixes.Count);

        return report;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Cleanup if needed
    }
}
