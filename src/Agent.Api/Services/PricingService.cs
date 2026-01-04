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
    /// Unknown tiers receive no discount (0%) and a warning is logged.
    /// </summary>
    public decimal CalculateDiscountedPrice(decimal basePrice, string customerTier)
    {
        logger.LogInformation("Calculating price for tier {Tier}, base price {Price}", customerTier, basePrice);

        var tierKey = customerTier?.ToLower() ?? string.Empty;
        if (!_tierDiscounts.TryGetValue(tierKey, out var discountRate))
        {
            logger.LogWarning("Unknown customer tier '{Tier}'. No discount applied. Valid tiers: {ValidTiers}", 
                customerTier, string.Join(", ", _tierDiscounts.Keys));
            discountRate = 0m; // No discount for unknown tiers
        }

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
