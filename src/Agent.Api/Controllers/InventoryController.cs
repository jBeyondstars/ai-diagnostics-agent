using Agent.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent.Api.Controllers;

/// <summary>
/// Controller for inventory operations.
/// Used to test scenarios where exception occurs HERE but the bug is in InventoryService.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class InventoryController(
    InventoryService inventoryService,
    ILogger<InventoryController> logger) : ControllerBase
{
    /// <summary>
    /// Calculates reorder urgency score for a product.
    ///
    /// EXCEPTION SCENARIO:
    /// - Use productId=999 (unknown product)
    /// - InventoryService.CalculateDaysUntilStockout returns 0 for unknown products
    /// - This method divides by that value → DivideByZeroException HERE
    /// - Stack trace shows THIS controller, but the fix is in InventoryService
    /// - Claude should need to use tools to find InventoryService.cs
    /// </summary>
    /// <param name="productId">Product ID. Use 999 to trigger the bug.</param>
    /// <param name="avgDailySales">Average daily sales (default: 5)</param>
    [HttpGet("{productId}/reorder-urgency")]
    [ProducesResponseType<ReorderUrgencyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult<ReorderUrgencyResponse> GetReorderUrgency(
        [FromRoute] int productId,
        [FromQuery] int avgDailySales = 5)
    {
        logger.LogInformation("Calculating reorder urgency for product {ProductId}", productId);

        // Get days until stockout from service
        // BUG IN SERVICE: Returns 0 for unknown products
        var daysUntilStockout = inventoryService.CalculateDaysUntilStockout(productId, avgDailySales);

        logger.LogInformation("Days until stockout: {Days}", daysUntilStockout);

        // Calculate urgency score (higher = more urgent)
        // BUG MANIFESTS HERE: Division by zero when daysUntilStockout is 0
        // Stack trace will show THIS line, but the fix should be in InventoryService
        var urgencyScore = 100 / daysUntilStockout;

        var urgencyLevel = urgencyScore switch
        {
            > 50 => "Critical",
            > 20 => "High",
            > 10 => "Medium",
            _ => "Low"
        };

        return Ok(new ReorderUrgencyResponse
        {
            ProductId = productId,
            DaysUntilStockout = daysUntilStockout,
            UrgencyScore = urgencyScore,
            UrgencyLevel = urgencyLevel,
            RecommendedAction = urgencyLevel == "Critical"
                ? "Reorder immediately"
                : $"Review in {daysUntilStockout} days"
        });
    }

    /// <summary>
    /// Gets inventory status with stock level details.
    ///
    /// EXCEPTION SCENARIO:
    /// - Use productId=999 (unknown product)
    /// - InventoryService.GetInventory returns null for unknown products
    /// - This method accesses .QuantityInStock → NullReferenceException HERE
    /// - Stack trace shows THIS controller, but fix could be in either place
    /// </summary>
    [HttpGet("{productId}/status")]
    [ProducesResponseType<InventoryStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult<InventoryStatusResponse> GetInventoryStatus([FromRoute] int productId)
    {
        logger.LogInformation("Getting inventory status for product {ProductId}", productId);

        // Get inventory from service - may return null for unknown products
        var inventory = inventoryService.GetInventory(productId);

        // BUG: No null check - will throw NullReferenceException
        // Stack trace shows THIS line, but root cause is service returning null
        var stockPercentage = (inventory.QuantityInStock * 100) / Math.Max(inventory.ReorderLevel * 5, 1);

        var status = inventory.QuantityInStock switch
        {
            0 => "Out of Stock",
            _ when inventory.QuantityInStock <= inventory.ReorderLevel => "Low Stock",
            _ => "In Stock"
        };

        return Ok(new InventoryStatusResponse
        {
            ProductId = productId,
            QuantityInStock = inventory.QuantityInStock,
            ReorderLevel = inventory.ReorderLevel,
            StockPercentage = stockPercentage,
            Status = status
        });
    }
}

public class ReorderUrgencyResponse
{
    public int ProductId { get; set; }
    public int DaysUntilStockout { get; set; }
    public int UrgencyScore { get; set; }
    public required string UrgencyLevel { get; set; }
    public required string RecommendedAction { get; set; }
}

public class InventoryStatusResponse
{
    public int ProductId { get; set; }
    public int QuantityInStock { get; set; }
    public int ReorderLevel { get; set; }
    public int StockPercentage { get; set; }
    public required string Status { get; set; }
}
