namespace Agent.Api.Services;

public sealed class InventoryService(ILogger<InventoryService> logger)
{
    private static readonly Dictionary<int, InventoryItem> _inventory = new()
    {
        [1] = new InventoryItem { ProductId = 1, QuantityInStock = 50, ReorderLevel = 10 },
        [2] = new InventoryItem { ProductId = 2, QuantityInStock = 0, ReorderLevel = 5 },
        [3] = new InventoryItem { ProductId = 3, QuantityInStock = 100, ReorderLevel = 20 }
    };

    public InventoryItem? GetInventory(int productId)
    {
        logger.LogInformation("Fetching inventory for product {ProductId}", productId);
        _inventory.TryGetValue(productId, out var item);
        return item; // Returns null for unknown products (Demo bug trigger)
    }

    public int CalculateDaysUntilStockout(int productId, int avgDailySales)
    {
        logger.LogInformation("Calculating stockout for product {ProductId}, avg sales {Sales}/day", productId, avgDailySales);

        var item = GetInventory(productId);
        
        // Demo bug: Returns 0 for missing items, causing DivideByZero in controller
        if (item is null) return 0;
        
        if (avgDailySales <= 0) return int.MaxValue;

        return item.QuantityInStock / avgDailySales;
    }
}

public record InventoryItem
{
    public int ProductId { get; init; }
    public int QuantityInStock { get; init; }
    public int ReorderLevel { get; init; }
}