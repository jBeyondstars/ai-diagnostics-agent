namespace Agent.Api.Services;

/// <summary>
/// Service for checking inventory levels.
/// </summary>
public class InventoryService(ILogger<InventoryService> logger)
{
    // Simulated inventory data
    private static readonly Dictionary<int, InventoryItem> _inventory = new()
    {
        [1] = new InventoryItem { ProductId = 1, QuantityInStock = 50, ReorderLevel = 10 },
        [2] = new InventoryItem { ProductId = 2, QuantityInStock = 0, ReorderLevel = 5 },  // Out of stock
        [3] = new InventoryItem { ProductId = 3, QuantityInStock = 100, ReorderLevel = 20 }
    };

    /// <summary>
    /// Gets inventory for a product.
    /// BUG: Returns null for unknown products instead of throwing or returning empty result.
    /// This causes NullReferenceException in callers who don't check for null.
    /// </summary>
    public InventoryItem? GetInventory(int productId)
    {
        logger.LogInformation("Fetching inventory for product {ProductId}", productId);

        // BUG: Returns null for unknown products - callers may not handle this
        _inventory.TryGetValue(productId, out var item);
        return item;
    }

    /// <summary>
    /// Calculates days until stockout based on average daily sales.
    /// BUG: Returns 0 for out-of-stock items instead of a meaningful value.
    /// </summary>
    public int CalculateDaysUntilStockout(int productId, int avgDailySales)
    {
        logger.LogInformation("Calculating stockout for product {ProductId}, avg sales {Sales}/day",
            productId, avgDailySales);

        var item = GetInventory(productId);
        if (item == null)
        {
            // Product not found - throw exception so callers know the product doesn't exist
            throw new KeyNotFoundException($"Product with ID {productId} not found in inventory.");
        }

        if (avgDailySales <= 0)
            return int.MaxValue;

        // Handle out-of-stock items: return 0 only for actual zero stock
        // Callers should check for this case before using as divisor
        if (item.QuantityInStock == 0)
            return 0;

        return item.QuantityInStock / avgDailySales;
    }
}

public class InventoryItem
{
    public int ProductId { get; set; }
    public int QuantityInStock { get; set; }
    public int ReorderLevel { get; set; }
}
