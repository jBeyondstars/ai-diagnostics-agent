using Agent.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent.Api.Controllers;

/// <summary>
/// Controller for product operations.
/// Used to test complex exception scenarios where Claude needs to investigate multiple files.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ProductsController(
    PricingService pricingService,
    ILogger<ProductsController> logger) : ControllerBase
{
    // Simulated product catalog
    private static readonly Dictionary<int, CatalogProduct> _products = new()
    {
        [1] = new CatalogProduct { Id = 1, Name = "Laptop Pro", BasePrice = 1299.99m, Category = "Electronics" },
        [2] = new CatalogProduct { Id = 2, Name = "Wireless Mouse", BasePrice = 49.99m, Category = "Electronics" },
        [3] = new CatalogProduct { Id = 3, Name = "USB-C Hub", BasePrice = 79.99m, Category = "Accessories" }
    };

    /// <summary>
    /// Gets a product with calculated price for customer tier.
    /// This endpoint will throw KeyNotFoundException for unknown tiers.
    /// The bug is in PricingService but stack trace shows this controller.
    /// Claude should need to use tools to find PricingService.cs
    /// </summary>
    /// <param name="id">Product ID (1-3)</param>
    /// <param name="customerTier">Customer tier: bronze, silver, gold, platinum. Use "vip" to trigger bug.</param>
    [HttpGet("{id}/price")]
    [ProducesResponseType<ProductPriceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult<ProductPriceResponse> GetProductPrice(int id, [FromQuery] string customerTier = "bronze")
    {
        logger.LogInformation("Getting price for product {ProductId} with tier {Tier}", id, customerTier);

        if (!_products.TryGetValue(id, out CatalogProduct? product))
        {
            return NotFound(new ProblemDetails
            {
                Title = "Product not found",
                Detail = $"No product found with ID {id}"
            });
        }

        // This will throw KeyNotFoundException if tier is invalid (e.g., "vip")
        // The exception originates in PricingService but Claude needs to find that file
        var finalPrice = pricingService.CalculateDiscountedPrice(product.BasePrice, customerTier);

        return Ok(new ProductPriceResponse
        {
            ProductId = product.Id,
            ProductName = product.Name,
            BasePrice = product.BasePrice,
            CustomerTier = customerTier,
            FinalPrice = finalPrice,
            AvailableTiers = pricingService.GetAvailableTiers()
        });
    }

    /// <summary>
    /// Calculates bulk order price. Another scenario requiring PricingService investigation.
    /// </summary>
    [HttpPost("bulk-price")]
    [ProducesResponseType<BulkPriceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult<BulkPriceResponse> CalculateBulkPrice([FromBody] BulkPriceRequest request)
    {
        logger.LogInformation("Calculating bulk price for {Count} items, tier {Tier}",
            request.Items.Count, request.CustomerTier);

        var results = new List<BulkPriceItem>();
        decimal totalBase = 0;
        decimal totalFinal = 0;

        foreach (var item in request.Items)
        {
            if (!_products.TryGetValue(item.ProductId, out CatalogProduct? product))
            {
                logger.LogWarning("Skipping unknown product {ProductId}", item.ProductId);
                continue;
            }

            var itemBase = product.BasePrice * item.Quantity;
            // BUG: Same issue - invalid tier will throw
            var itemFinal = pricingService.CalculateDiscountedPrice(itemBase, request.CustomerTier);

            totalBase += itemBase;
            totalFinal += itemFinal;

            results.Add(new BulkPriceItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = item.Quantity,
                UnitPrice = product.BasePrice,
                LineTotal = itemFinal
            });
        }

        return Ok(new BulkPriceResponse
        {
            Items = results,
            TotalBasePrice = totalBase,
            TotalFinalPrice = totalFinal,
            Savings = totalBase - totalFinal
        });
    }
}

public class CatalogProduct
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public decimal BasePrice { get; set; }
    public required string Category { get; set; }
}

public class ProductPriceResponse
{
    public int ProductId { get; set; }
    public required string ProductName { get; set; }
    public decimal BasePrice { get; set; }
    public required string CustomerTier { get; set; }
    public decimal FinalPrice { get; set; }
    public required IReadOnlyCollection<string> AvailableTiers { get; set; }
}

public class BulkPriceRequest
{
    public required List<BulkPriceRequestItem> Items { get; set; }
    public required string CustomerTier { get; set; }
}

public class BulkPriceRequestItem
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}

public class BulkPriceResponse
{
    public required List<BulkPriceItem> Items { get; set; }
    public decimal TotalBasePrice { get; set; }
    public decimal TotalFinalPrice { get; set; }
    public decimal Savings { get; set; }
}

public class BulkPriceItem
{
    public int ProductId { get; set; }
    public required string ProductName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}
