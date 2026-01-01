using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Octokit;
using System.ComponentModel;
using System.Text.Json;

namespace Agent.Plugins;

/// <summary>
/// Semantic Kernel plugin for interacting with GitHub.
/// This is the "hands" of our agent - it can read code and create PRs.
/// </summary>
public sealed class GitHubPlugin(
    string token,
    string owner,
    string repo,
    string defaultBranch = "main",
    ILogger<GitHubPlugin>? logger = null)
{
    private readonly GitHubClient _client = new(new ProductHeaderValue("AI-Diagnostics-Agent"))
    {
        Credentials = new Credentials(token)
    };

    private readonly ILogger<GitHubPlugin> _logger = logger
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubPlugin>.Instance;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Gets the content of a file from the repository
    /// </summary>
    [KernelFunction("get_file_content")]
    [Description("Retrieves the content of a source file from the GitHub repository. Use this to read code that needs to be analyzed or fixed.")]
    public async Task<string> GetFileContentAsync(
        [Description("Path to the file relative to repository root (e.g., 'src/Services/OrderService.cs')")]
        string filePath,
        [Description("Branch to read from (defaults to main)")]
        string? branch = null)
    {
        branch ??= defaultBranch;
        _logger.LogInformation("Getting file content: {FilePath} from branch {Branch}", filePath, branch);

        try
        {
            var contents = await _client.Repository.Content.GetAllContentsByRef(owner, repo, filePath, branch);

            if (contents.Count > 0)
            {
                var file = contents[0];
                _logger.LogInformation("GitHub returned file: Path={Path}, Size={Size}, ContentLength={ContentLength}, EncodingType={Encoding}",
                    file.Path, file.Size, file.Content?.Length ?? 0, file.Encoding);

                if (!string.IsNullOrEmpty(file.Content))
                {
                    try
                    {
                        var decodedContent = System.Text.Encoding.UTF8.GetString(
                            Convert.FromBase64String(file.Content));

                        _logger.LogInformation("Successfully decoded Base64 content: {Length} chars", decodedContent.Length);
                        return JsonSerializer.Serialize(new
                        {
                            path = file.Path,
                            sha = file.Sha,
                            content = decodedContent,
                            size = file.Size,
                            encoding = "utf-8"
                        }, JsonOptions);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogWarning("Base64 decoding failed for {FilePath}: {Error}. Content preview: {Preview}",
                            filePath, ex.Message, file.Content?[..Math.Min(100, file.Content?.Length ?? 0)]);
                    }
                }

                _logger.LogInformation("Fetching raw content for {FilePath}", filePath);
                var rawContent = await _client.Repository.Content.GetRawContentByRef(owner, repo, filePath, branch);
                _logger.LogInformation("Got raw content: {Length} bytes", rawContent.Length);

                return JsonSerializer.Serialize(new
                {
                    path = file.Path,
                    sha = file.Sha,
                    content = System.Text.Encoding.UTF8.GetString(rawContent),
                    size = rawContent.Length,
                    encoding = "utf-8"
                }, JsonOptions);
            }

            return JsonSerializer.Serialize(new { error = "File not found" }, JsonOptions);
        }
        catch (NotFoundException)
        {
            _logger.LogWarning("File not found: {FilePath}", filePath);
            return JsonSerializer.Serialize(new { error = $"File not found: {filePath}" }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting file content");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// Searches for code in the repository
    /// </summary>
    [KernelFunction("search_code")]
    [Description("Searches for code snippets in the repository. Useful for finding where a class or method is defined.")]
    public async Task<string> SearchCodeAsync(
        [Description("Search query (e.g., 'class OrderService' or 'NullReferenceException')")]
        string query,
        [Description("File extension filter (e.g., 'cs' for C# files)")]
        string? extension = null,
        [Description("Maximum results to return")]
        int maxResults = 10)
    {
        _logger.LogInformation("Searching code: {Query}", query);

        try
        {
            var searchQuery = $"{query} repo:{owner}/{repo}";
            if (!string.IsNullOrEmpty(extension))
            {
                searchQuery += $" extension:{extension}";
            }

            var request = new SearchCodeRequest(searchQuery) { PerPage = maxResults };
            var results = await _client.Search.SearchCode(request);

            List<object> matches = [.. results.Items.Select(item => new
            {
                path = item.Path,
                name = item.Name,
                url = item.HtmlUrl
            })];

            _logger.LogInformation("Found {Count} matches", matches.Count);

            return JsonSerializer.Serialize(new { totalCount = results.TotalCount, matches }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching code");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// Gets the content of multiple files efficiently
    /// </summary>
    [KernelFunction("get_multiple_files")]
    [Description("Retrieves content of multiple files in a single call. More efficient than calling get_file_content multiple times.")]
    public async Task<string> GetMultipleFilesAsync(
        [Description("Comma-separated list of file paths (e.g., 'src/A.cs,src/B.cs')")]
        string filePaths,
        [Description("Branch to read from (defaults to main)")]
        string? branch = null)
    {
        branch ??= defaultBranch;
        var paths = filePaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _logger.LogInformation("Getting {Count} files from branch {Branch}", paths.Length, branch);

        var results = new List<object>();

        foreach (var path in paths)
        {
            try
            {
                var contents = await _client.Repository.Content.GetAllContentsByRef(owner, repo, path, branch);

                if (contents.Count > 0)
                {
                    var file = contents[0];
                    var content = !string.IsNullOrEmpty(file.Content)
                        ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(file.Content))
                        : System.Text.Encoding.UTF8.GetString(
                            await _client.Repository.Content.GetRawContentByRef(owner, repo, path, branch));

                    results.Add(new { path, sha = file.Sha, content, success = true });
                }
                else
                {
                    results.Add(new { path, error = "File not found", success = false });
                }
            }
            catch (Exception ex)
            {
                results.Add(new { path, error = ex.Message, success = false });
            }
        }

        return JsonSerializer.Serialize(new { files = results }, JsonOptions);
    }

    /// <summary>
    /// Creates a Pull Request with code fixes
    /// </summary>
    [KernelFunction("create_pull_request")]
    [Description("Creates a Pull Request with the proposed code fixes. Use this after analyzing exceptions and generating fixes.")]
    public async Task<string> CreatePullRequestAsync(
        [Description("Title for the Pull Request")]
        string title,
        [Description("Detailed description of the changes and why they fix the issue")]
        string description,
        [Description("JSON array of file changes: [{\"path\": \"src/file.cs\", \"content\": \"new content\"}]")]
        string filesJson)
    {
        _logger.LogInformation("Creating Pull Request: {Title}", title);

        try
        {
            var files = JsonSerializer.Deserialize<List<FileChangeDto>>(filesJson, JsonOptions)
                ?? throw new ArgumentException("Invalid files JSON format");

            if (files.Count == 0)
            {
                return JsonSerializer.Serialize(new { error = "No files to change" }, JsonOptions);
            }

            var branchName = $"fix/ai-agent-{DateTime.UtcNow:yyyyMMdd-HHmmss}";

            var mainRef = await _client.Git.Reference.Get(owner, repo, $"heads/{defaultBranch}");
            await _client.Git.Reference.Create(owner, repo, new NewReference(
                $"refs/heads/{branchName}",
                mainRef.Object.Sha));

            _logger.LogInformation("Created branch: {Branch}", branchName);

            foreach (var file in files)
            {
                try
                {
                    var existingFiles = await _client.Repository.Content.GetAllContentsByRef(
                        owner, repo, file.Path, branchName);

                    if (existingFiles.Count > 0)
                    {
                        await _client.Repository.Content.UpdateFile(
                            owner, repo, file.Path,
                            new UpdateFileRequest(
                                $"fix: {Path.GetFileName(file.Path)} - AI Agent auto-fix",
                                file.Content,
                                existingFiles[0].Sha,
                                branchName));

                        _logger.LogInformation("Updated file: {Path}", file.Path);
                    }
                }
                catch (NotFoundException)
                {
                    await _client.Repository.Content.CreateFile(
                        owner, repo, file.Path,
                        new CreateFileRequest(
                            $"fix: {Path.GetFileName(file.Path)} - AI Agent auto-fix",
                            file.Content,
                            branchName));

                    _logger.LogInformation("Created file: {Path}", file.Path);
                }
            }

            var prBody = $"""
                ## Auto-generated by AI Diagnostics Agent

                {description}

                ### Files Changed
                {string.Join("\n", files.Select(f => $"- `{f.Path}`"))}

                ---

                > **This PR was automatically generated.** Please review carefully before merging.
                >
                > The AI agent analyzed Application Insights logs and proposed these fixes based on
                > exception patterns and stack traces.
                """;

            var pr = await _client.PullRequest.Create(owner, repo, new NewPullRequest(
                title,
                branchName,
                defaultBranch)
            {
                Body = prBody
            });

            _logger.LogInformation("Created PR #{Number}: {Url}", pr.Number, pr.HtmlUrl);

            return JsonSerializer.Serialize(new
            {
                success = true,
                pullRequestNumber = pr.Number,
                pullRequestUrl = pr.HtmlUrl,
                branch = branchName,
                filesChanged = files.Count
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Pull Request");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// Creates a GitHub Issue to document a problem
    /// </summary>
    [KernelFunction("create_issue")]
    [Description("Creates a GitHub Issue to document a problem that was detected. Use this when a fix cannot be automatically generated.")]
    public async Task<string> CreateIssueAsync(
        [Description("Title for the Issue")]
        string title,
        [Description("Detailed description of the problem, including exception details and analysis")]
        string description,
        [Description("Comma-separated labels (e.g., 'bug,high-priority,ai-detected')")]
        string labels = "bug,ai-detected")
    {
        _logger.LogInformation("Creating Issue: {Title}", title);

        try
        {
            var issueBody = $"""
                ## Issue Detected by AI Diagnostics Agent

                {description}

                ---

                > This issue was automatically created based on Application Insights analysis.
                > Review the details above and take appropriate action.
                """;

            var newIssue = new NewIssue(title) { Body = issueBody };

            foreach (var label in labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                newIssue.Labels.Add(label);
            }

            var issue = await _client.Issue.Create(owner, repo, newIssue);

            _logger.LogInformation("Created Issue #{Number}: {Url}", issue.Number, issue.HtmlUrl);

            return JsonSerializer.Serialize(new
            {
                success = true,
                issueNumber = issue.Number,
                issueUrl = issue.HtmlUrl
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Issue");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// Gets recent commits to understand recent changes
    /// </summary>
    [KernelFunction("get_recent_commits")]
    [Description("Gets recent commits to understand what changed recently. Useful for correlating exceptions with deployments.")]
    public async Task<string> GetRecentCommitsAsync(
        [Description("Number of commits to retrieve")]
        int count = 10,
        [Description("Path filter (optional, e.g., 'src/Services')")]
        string? path = null)
    {
        _logger.LogInformation("Getting recent commits");

        try
        {
            var request = new CommitRequest { Sha = defaultBranch };

            if (!string.IsNullOrEmpty(path))
            {
                request.Path = path;
            }

            var commits = await _client.Repository.Commit.GetAll(owner, repo, request);

            List<object> results = [.. commits.Take(count).Select(c => new
            {
                sha = c.Sha[..7],
                message = c.Commit.Message.Split('\n')[0],
                author = c.Commit.Author.Name,
                date = c.Commit.Author.Date.ToString("yyyy-MM-dd HH:mm"),
                url = c.HtmlUrl
            })];

            return JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting commits");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// Gets open issues and PRs related to a topic
    /// </summary>
    [KernelFunction("search_issues")]
    [Description("Searches for existing issues and PRs related to a topic. Use this to avoid creating duplicates.")]
    public async Task<string> SearchIssuesAsync(
        [Description("Search query (e.g., 'NullReferenceException' or 'OrderService')")]
        string query,
        [Description("State filter: open, closed, or all")]
        string state = "open")
    {
        _logger.LogInformation("Searching issues: {Query}", query);

        try
        {
            var searchQuery = $"{query} repo:{owner}/{repo}";

            if (state != "all")
            {
                searchQuery += $" state:{state}";
            }

            var request = new SearchIssuesRequest(searchQuery) { PerPage = 10 };
            var results = await _client.Search.SearchIssues(request);

            var issues = results.Items.Select(i => new
            {
                number = i.Number,
                title = i.Title,
                state = i.State.StringValue,
                isPullRequest = i.PullRequest is not null,
                url = i.HtmlUrl,
                createdAt = i.CreatedAt.ToString("yyyy-MM-dd"),
                labels = (List<string>)[.. i.Labels.Select(l => l.Name)]
            }).ToList();

            return JsonSerializer.Serialize(new { totalCount = results.TotalCount, issues }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching issues");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    /// <summary>
    /// Checks if there's an existing open PR for a specific exception.
    /// Uses a hybrid approach: checks by ProblemId, then by source file + exception type.
    /// </summary>
    public async Task<ExistingPrResult> CheckExistingPrAsync(
        string exceptionType,
        string? problemId = null,
        string? sourceFile = null)
    {
        _logger.LogInformation("Checking for existing PR: Type={ExceptionType}, ProblemId={ProblemId}, File={SourceFile}",
            exceptionType, problemId ?? "null", sourceFile ?? "null");

        try
        {
            // Get all open PRs created by the AI agent
            var prs = await _client.PullRequest.GetAllForRepository(
                owner,
                repo,
                new PullRequestRequest { State = ItemStateFilter.Open });

            // Filter to AI agent PRs only
            List<PullRequest> agentPrs = [.. prs.Where(pr => pr.Head.Ref.StartsWith("fix/ai-agent-"))];

            if (agentPrs.Count == 0)
            {
                _logger.LogInformation("No AI agent PRs found");
                return new ExistingPrResult(Exists: false, PrNumber: null, PrUrl: null, PrTitle: null);
            }

            var shortType = exceptionType.Split('.').Last();

            // Strategy 1: Check by exact ProblemId
            if (!string.IsNullOrEmpty(problemId))
            {
                var prByProblemId = agentPrs.FirstOrDefault(pr =>
                    pr.Body?.Contains($"ProblemId: `{problemId}`", StringComparison.OrdinalIgnoreCase) == true);

                if (prByProblemId != null)
                {
                    _logger.LogInformation("Found existing PR #{Number} by ProblemId: {Url}",
                        prByProblemId.Number, prByProblemId.HtmlUrl);
                    return new ExistingPrResult(true, prByProblemId.Number, prByProblemId.HtmlUrl, prByProblemId.Title);
                }
            }

            // Strategy 2: Check by source file + exception type (covers old PRs without ProblemId)
            if (!string.IsNullOrEmpty(sourceFile))
            {
                var fileName = Path.GetFileName(sourceFile);
                var prByFileAndType = agentPrs.FirstOrDefault(pr =>
                    pr.Body != null &&
                    pr.Body.Contains(fileName, StringComparison.OrdinalIgnoreCase) &&
                    (pr.Title.Contains(shortType, StringComparison.OrdinalIgnoreCase) ||
                     pr.Body.Contains(exceptionType, StringComparison.OrdinalIgnoreCase)));

                if (prByFileAndType != null)
                {
                    _logger.LogInformation("Found existing PR #{Number} by file+type ({File} + {Type}): {Url}",
                        prByFileAndType.Number, fileName, shortType, prByFileAndType.HtmlUrl);
                    return new ExistingPrResult(true, prByFileAndType.Number, prByFileAndType.HtmlUrl, prByFileAndType.Title);
                }
            }

            _logger.LogInformation("No existing PR found for {ExceptionType} in {SourceFile}",
                exceptionType, sourceFile ?? "unknown");
            return new ExistingPrResult(Exists: false, PrNumber: null, PrUrl: null, PrTitle: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking for existing PR, allowing creation");
            return new ExistingPrResult(Exists: false, PrNumber: null, PrUrl: null, PrTitle: null);
        }
    }

    public record ExistingPrResult(bool Exists, int? PrNumber, string? PrUrl, string? PrTitle);

    /// <summary>
    /// Gets the diff between two commits or branches
    /// </summary>
    [KernelFunction("get_diff")]
    [Description("Gets the diff between two commits or branches to understand what changed")]
    public async Task<string> GetDiffAsync(
        [Description("Base commit/branch to compare from")]
        string baseRef,
        [Description("Head commit/branch to compare to (defaults to default branch)")]
        string? headRef = null)
    {
        headRef ??= defaultBranch;
        _logger.LogInformation("Getting diff between {Base} and {Head}", baseRef, headRef);

        try
        {
            var comparison = await _client.Repository.Commit.Compare(owner, repo, baseRef, headRef);

            List<object> files = [.. comparison.Files.Select(f => new
            {
                filename = f.Filename,
                status = f.Status,
                additions = f.Additions,
                deletions = f.Deletions,
                changes = f.Changes,
                patch = f.Patch?.Length > 2000 ? f.Patch[..2000] + "\n... (truncated)" : f.Patch
            })];

            return JsonSerializer.Serialize(new
            {
                status = comparison.Status,
                aheadBy = comparison.AheadBy,
                behindBy = comparison.BehindBy,
                totalCommits = comparison.TotalCommits,
                files
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting diff");
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    private sealed record FileChangeDto(string Path, string Content);
}
