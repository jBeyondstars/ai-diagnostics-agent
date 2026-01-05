using Agent.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agent.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ProductsController(
    PricingService pricingService,
    ILogger<ProductsController> logger) : ControllerBase
{
    private static readonly Dictionary<int, CatalogProduct> _products = new()
    {
        [1] = new CatalogProduct { Id = 1, Name = "Laptop Pro", BasePrice = 1299.99m, Category = "Electronics" },
        [2] = new CatalogProduct { Id = 2, Name = "Wireless Mouse", BasePrice = 49.99m, Category = "Electronics" },
        [3] = new CatalogProduct { Id = 3, Name = "USB-C Hub", BasePrice = 79.99m, Category = "Accessories" }
    };

    [HttpGet("{id}/price")]
    [ProducesResponseType<ProductPriceResponse>(StatusCodes.Status200OK)]
    public ActionResult<ProductPriceResponse> GetProductPrice([FromRoute] int id, [FromQuery] string customerTier = "bronze")
    {
        logger.LogInformation("Getting price for product {ProductId} with tier {Tier}", id, customerTier);

        if (!_products.TryGetValue(id, out var product))
        {
            return NotFound(new ProblemDetails { Title = "Product not found", Detail = $"ID {id}" });
        }

        // Demo Bug: throws KeyNotFoundException if tier is invalid
        var finalPrice = pricingService.CalculateDiscountedPrice(product.BasePrice, customerTier);

        return Ok(new ProductPriceResponse(
            product.Id,
            product.Name,
            product.BasePrice,
            customerTier,
            finalPrice,
            pricingService.GetAvailableTiers()
        ));
    }

    [HttpPost("bulk-price")]
    public ActionResult<BulkPriceResponse> CalculateBulkPrice([FromBody] BulkPriceRequest request)
    {
        logger.LogInformation("Calculating bulk price for {Count} items, tier {Tier}", request.Items.Count, request.CustomerTier);

        var results = new List<BulkPriceItem>();
        decimal totalBase = 0;
        decimal totalFinal = 0;

        foreach (var item in request.Items)
        {
            if (!_products.TryGetValue(item.ProductId, out var product)) continue;

            var itemBase = product.BasePrice * item.Quantity;
            // Demo Bug: invalid tier throws
            var itemFinal = pricingService.CalculateDiscountedPrice(itemBase, request.CustomerTier);

            totalBase += itemBase;
            totalFinal += itemFinal;

            results.Add(new BulkPriceItem(product.Id, product.Name, item.Quantity, product.BasePrice, itemFinal));
        }

        return Ok(new BulkPriceResponse(results, totalBase, totalFinal, totalBase - totalFinal));
    }
}

public record CatalogProduct
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public decimal BasePrice { get; init; }
    public required string Category { get; init; }
}

public record ProductPriceResponse(
    int ProductId,
    string ProductName,
    decimal BasePrice,
    string CustomerTier,
    decimal FinalPrice,
    IReadOnlyCollection<string> AvailableTiers);

public record BulkPriceRequest(List<BulkPriceRequestItem> Items, string CustomerTier);
public record BulkPriceRequestItem(int ProductId, int Quantity);
public record BulkPriceResponse(List<BulkPriceItem> Items, decimal TotalBasePrice, decimal TotalFinalPrice, decimal Savings);
public record BulkPriceItem(int ProductId, string ProductName, int Quantity, decimal UnitPrice, decimal LineTotal);