namespace Agent.Api.Services;

/// <summary>
/// Service for calculating product prices with discounts.
/// </summary>
public class PricingService(ILogger<PricingService> logger)
{
    // Discount rules by customer tier
    private static readonly Dictionary<string, decimal> _tierDiscounts = new()
    {
        ["bronze"] = 0.05m,
        ["silver"] = 0.10m,
        ["gold"] = 0.15m,
        ["platinum"] = 0.20m
    };

    /// <summary>
    /// Calculates the final price after applying tier discount.
    /// BUG: Does not handle unknown tiers - throws KeyNotFoundException
    /// </summary>
    public decimal CalculateDiscountedPrice(decimal basePrice, string customerTier)
    {
        logger.LogInformation("Calculating price for tier {Tier}, base price {Price}", customerTier, basePrice);

        // BUG: No validation of customerTier - will throw KeyNotFoundException for unknown tiers
        var discountRate = _tierDiscounts[customerTier.ToLower()];

        var discount = basePrice * discountRate;
        var finalPrice = basePrice - discount;

        logger.LogInformation("Applied {Rate:P0} discount: {Final}", discountRate, finalPrice);

        return finalPrice;
    }

    /// <summary>
    /// Gets available discount tiers.
    /// </summary>
    public IReadOnlyCollection<string> GetAvailableTiers() => _tierDiscounts.Keys.ToList();
}
