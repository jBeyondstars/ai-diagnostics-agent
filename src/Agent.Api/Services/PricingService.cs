namespace Agent.Api.Services;

/// <summary>
/// Service for calculating product prices with discounts.
/// </summary>
public sealed class PricingService(ILogger<PricingService> logger)
{
    private static readonly Dictionary<string, decimal> _tierDiscounts = new()
    {
        ["bronze"] = 0.05m,
        ["silver"] = 0.10m,
        ["gold"] = 0.15m,
        ["platinum"] = 0.20m
    };

    public decimal CalculateDiscountedPrice(decimal basePrice, string customerTier)
    {
        logger.LogInformation("Calculating price for tier {Tier}, base price {Price}", customerTier, basePrice);

        var normalizedTier = customerTier?.ToLower() ?? string.Empty;
        if (!_tierDiscounts.TryGetValue(normalizedTier, out var discountRate))
        {
            logger.LogWarning("Unknown customer tier '{Tier}', applying no discount", customerTier);
            discountRate = 0m; // No discount for unknown tiers
        }
        return basePrice * (1 - discountRate);
    }

    public IReadOnlyCollection<string> GetAvailableTiers() => _tierDiscounts.Keys.ToList();
}