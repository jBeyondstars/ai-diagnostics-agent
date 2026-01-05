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

        // Demo Bug: Throws KeyNotFoundException for unknown tiers
        var discountRate = _tierDiscounts[customerTier.ToLower()];
        return basePrice * (1 - discountRate);
    }

    public IReadOnlyCollection<string> GetAvailableTiers() => _tierDiscounts.Keys.ToList();
}