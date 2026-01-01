using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Agent.Core.Services;

public class DeduplicationService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<DeduplicationService> _logger;
    private readonly TimeSpan _defaultCooldown = TimeSpan.FromHours(6);

    public DeduplicationService(IDistributedCache cache, ILogger<DeduplicationService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Check if an exception should be analyzed based on its problemId.
    /// Returns true if it should be analyzed, false if it was recently analyzed.
    /// </summary>
    public async Task<bool> ShouldAnalyzeAsync(string problemId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(problemId))
            return true;

        var key = GetCacheKey(problemId);

        try
        {
            var existing = await _cache.GetStringAsync(key, cancellationToken);

            if (existing != null)
            {
                _logger.LogInformation("Skipping analysis for {ProblemId} - analyzed at {Time}",
                    problemId, existing);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis check failed for {ProblemId}, allowing analysis", problemId);
            return true;
        }
    }

    /// <summary>
    /// Mark an exception as analyzed to prevent re-analysis within the cooldown period.
    /// </summary>
    public async Task MarkAsAnalyzedAsync(string problemId, TimeSpan? cooldown = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(problemId))
            return;

        var key = GetCacheKey(problemId);
        var duration = cooldown ?? _defaultCooldown;

        try
        {
            await _cache.SetStringAsync(
                key,
                DateTime.UtcNow.ToString("O"),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = duration
                },
                cancellationToken);

            _logger.LogInformation("Marked {ProblemId} as analyzed (cooldown: {Hours}h)",
                problemId, duration.TotalHours);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark {ProblemId} as analyzed in Redis", problemId);
        }
    }

    /// <summary>
    /// Clear the analysis marker for a specific exception (e.g., if PR was rejected).
    /// </summary>
    public async Task ClearAsync(string problemId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(problemId))
            return;

        var key = GetCacheKey(problemId);

        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
            _logger.LogInformation("Cleared analysis marker for {ProblemId}", problemId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear {ProblemId} from Redis", problemId);
        }
    }

    private static string GetCacheKey(string problemId) => $"analyzed:{problemId}";
}
