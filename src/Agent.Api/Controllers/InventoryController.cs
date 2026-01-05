using Agent.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent.Api.Controllers;

/// <summary>
/// Controller with intentional bugs for the Agent to diagnose.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class InventoryController(
    InventoryService inventoryService,
    ILogger<InventoryController> logger) : ControllerBase
{
    [HttpGet("{productId}/reorder-urgency")]
    [ProducesResponseType<ReorderUrgencyResponse>(StatusCodes.Status200OK)]
    public ActionResult<ReorderUrgencyResponse> GetReorderUrgency(
        [FromRoute] int productId,
        [FromQuery] int avgDailySales = 5)
    {
        logger.LogInformation("Calculating reorder urgency for product {ProductId}", productId);

        var daysUntilStockout = inventoryService.CalculateDaysUntilStockout(productId, avgDailySales);
        
        // DEMO BUG: Intentional DivideByZeroException when daysUntilStockout is 0
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
            RecommendedAction = urgencyLevel == "Critical" ? "Reorder immediately" : $"Review in {daysUntilStockout} days"
        });
    }

    [HttpGet("{productId}/status")]
    [ProducesResponseType<InventoryStatusResponse>(StatusCodes.Status200OK)]
    public ActionResult<InventoryStatusResponse> GetInventoryStatus([FromRoute] int productId)
    {
        logger.LogInformation("Getting inventory status for product {ProductId}", productId);

        var inventory = inventoryService.GetInventory(productId);

        // DEMO BUG: Intentional NullReferenceException (inventory is null for unknown products)
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

public record ReorderUrgencyResponse
{
    public int ProductId { get; init; }
    public int DaysUntilStockout { get; init; }
    public int UrgencyScore { get; init; }
    public required string UrgencyLevel { get; init; }
    public required string RecommendedAction { get; init; }
}

public record InventoryStatusResponse
{
    public int ProductId { get; init; }
    public int QuantityInStock { get; init; }
    public int ReorderLevel { get; init; }
    public int StockPercentage { get; init; }
    public required string Status { get; init; }
}