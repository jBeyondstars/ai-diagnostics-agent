using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Agent.Core.Services;

public class DeduplicationService(IDistributedCache cache, ILogger<DeduplicationService> logger)
{
    private readonly TimeSpan _defaultCooldown = TimeSpan.FromHours(6);

    public async Task<bool> ShouldAnalyzeAsync(string problemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(problemId)) return true;

        try
        {
            var existing = await cache.GetStringAsync(GetCacheKey(problemId), ct);
            if (existing is null) return true;

            logger.LogInformation("Skipping analysis for {ProblemId} - analyzed at {Time}", problemId, existing);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis check failed for {ProblemId}, allowing analysis", problemId);
            return true;
        }
    }

    public async Task MarkAsAnalyzedAsync(string problemId, TimeSpan? cooldown = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(problemId)) return;

        var duration = cooldown ?? _defaultCooldown;
        try
        {
            await cache.SetStringAsync(
                GetCacheKey(problemId),
                DateTime.UtcNow.ToString("O"),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = duration },
                ct);

            logger.LogInformation("Marked {ProblemId} as analyzed (cooldown: {Hours}h)", problemId, duration.TotalHours);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to mark {ProblemId} as analyzed", problemId);
        }
    }

    public async Task ClearAsync(string problemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(problemId)) return;
        
        try
        {
            await cache.RemoveAsync(GetCacheKey(problemId), ct);
            logger.LogInformation("Cleared analysis marker for {ProblemId}", problemId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear {ProblemId}", problemId);
        }
    }

    public async Task ClearByPatternAsync(string pattern, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pattern)) return;

        try
        {
            var indexKey = GetIndexKey(pattern);
            var indexValue = await cache.GetStringAsync(indexKey, ct);

            if (!string.IsNullOrEmpty(indexValue))
            {
                var problemIds = indexValue.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var id in problemIds)
                {
                    await cache.RemoveAsync(GetCacheKey(id.Trim()), ct);
                }
                await cache.RemoveAsync(indexKey, ct);
                logger.LogInformation("Cleared {Count} entries for pattern {Pattern}", problemIds.Length, pattern);
            }
            else
            {
                await cache.RemoveAsync(GetCacheKey(pattern), ct);
                logger.LogInformation("Cleared simple key for pattern {Pattern}", pattern);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear pattern {Pattern}", pattern);
        }
    }

    public async Task AddToIndexAsync(string exceptionType, string problemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(exceptionType) || string.IsNullOrEmpty(problemId)) return;

        try
        {
            var indexKey = GetIndexKey(exceptionType);
            var existing = await cache.GetStringAsync(indexKey, ct) ?? "";

            if (!existing.Contains(problemId))
            {
                var newValue = string.IsNullOrEmpty(existing) ? problemId : $"{existing},{problemId}";
                await cache.SetStringAsync(
                    indexKey, 
                    newValue, 
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(48) }, 
                    ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to add {ProblemId} to index for {Type}", problemId, exceptionType);
        }
    }

    private static string GetCacheKey(string problemId) => $"analyzed:{problemId}";
    private static string GetIndexKey(string exceptionType) => $"analyzed:index:{exceptionType}";
}