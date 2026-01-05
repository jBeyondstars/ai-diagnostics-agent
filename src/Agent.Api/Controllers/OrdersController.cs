using Microsoft.AspNetCore.Mvc;

namespace Agent.Api.Controllers;

/// <summary>
/// Orders controller with intentional bugs for AI diagnostics testing.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class OrdersController(ILogger<OrdersController> logger) : ControllerBase
{
    private static readonly Dictionary<int, OrderEntity> _orders = new()
    {
        [1001] = new OrderEntity { Id = 1001, CustomerId = 1, Items = [new OrderItem { ProductId = 101, Name = "Laptop", Price = 999.99m, Quantity = 1 }], Status = "Completed" },
        [1002] = new OrderEntity { Id = 1002, CustomerId = 2, Items = [new OrderItem { ProductId = 102, Name = "Mouse", Price = 29.99m, Quantity = 2 }], Status = "Pending", Discount = new Discount { Code = "SAVE10", Percentage = 10 } },
        [1003] = new OrderEntity { Id = 1003, CustomerId = 3, Items = [], Status = "Draft" } // Empty items -> bugs
    };

    private static readonly Dictionary<int, Customer> _customers = new()
    {
        [1] = new Customer { Id = 1, Name = "Alice Smith", Email = "alice@example.com", Address = new Address { City = "Paris", Country = "France" } },
        [2] = new Customer { Id = 2, Name = "Bob Jones", Email = "bob@example.com", Address = null }, // Null address -> bug
        [3] = new Customer { Id = 3, Name = "Charlie Brown", Email = "charlie@example.com", Address = new Address { City = "London", Country = "UK" } }
    };

    private static readonly Dictionary<int, Product> _products = new()
    {
        [101] = new Product { Id = 101, Name = "Laptop", Stock = 50, Category = "Electronics" },
        [102] = new Product { Id = 102, Name = "Mouse", Stock = 200, Category = "Electronics" },
        [103] = new Product { Id = 103, Name = "Keyboard", Stock = 0, Category = "Electronics" }
    };

    [HttpGet("{orderId:int}/shipping")]
    public ActionResult<ShippingInfo> GetShippingInfo([FromRoute] int orderId)
    {
        logger.LogInformation("Getting shipping info for order {OrderId}", orderId);
        if (!_orders.TryGetValue(orderId, out var order)) return NotFound();

        var customer = _customers[order.CustomerId];

        // Demo Bug: customer.Address can be null
        return Ok(new ShippingInfo(
            orderId,
            customer.Name,
            customer.Address.City,
            customer.Address.Country,
            customer.Address.Country == "France" ? 2 : 5
        ));
    }

    [HttpGet("{orderId:int}/first-item")]
    public ActionResult<OrderItemDto> GetFirstItem([FromRoute] int orderId)
    {
        logger.LogInformation("Getting first item for order {OrderId}", orderId);
        if (!_orders.TryGetValue(orderId, out var order)) return NotFound();

        // Demo Bug: .First() on empty list
        var firstItem = order.Items.First();

        return Ok(new OrderItemDto(firstItem.ProductId, firstItem.Name, firstItem.Price, firstItem.Quantity));
    }

    [HttpGet("{orderId:int}/discount-rate")]
    public ActionResult<DiscountResult> CalculateDiscountRate([FromRoute] int orderId)
    {
        logger.LogInformation("Calculating discount rate for order {OrderId}", orderId);
        if (!_orders.TryGetValue(orderId, out var order)) return NotFound();

        var subtotal = order.Items.Sum(i => i.Price * i.Quantity);
        var discountAmount = order.Discount?.Percentage ?? 0;

        // Handle zero subtotal to avoid divide by zero - effective rate is 0 when there are no items
        var effectiveRate = subtotal == 0 ? 0 : (discountAmount * 100) / subtotal;

        return Ok(new DiscountResult(orderId, subtotal, discountAmount, effectiveRate));
    }

    [HttpGet("products/{productId:int}/stock")]
    public ActionResult<StockInfo> GetProductStock([FromRoute] int productId)
    {
        logger.LogInformation("Checking stock for product {ProductId}", productId);
        
        // Demo Bug: Missing TryGetValue check
        var product = _products[productId];

        var stockStatus = product.Stock switch
        {
            0 => "Out of Stock",
            < 10 => "Low Stock",
            < 50 => "In Stock",
            _ => "Well Stocked"
        };

        return Ok(new StockInfo(productId, product.Name, product.Stock, stockStatus));
    }

    [HttpGet("customer/{customerId:int}/average")]
    public ActionResult<CustomerStats> GetCustomerAverageOrderValue([FromRoute] int customerId)
    {
        logger.LogInformation("Calculating average order value for customer {CustomerId}", customerId);

        var customerOrders = _orders.Values.Where(o => o.CustomerId == customerId).ToList();
        var orderTotals = customerOrders.Select(o => o.Items.Sum(i => i.Price * i.Quantity)).ToArray();

        // Demo Bug: Index 0 access on empty array
        return Ok(new CustomerStats(
            customerId,
            customerOrders.Count,
            orderTotals[0],
            orderTotals[^1],
            orderTotals.Sum() / orderTotals.Length
        ));
    }
}

public record OrderEntity
{
    public int Id { get; init; }
    public int CustomerId { get; init; }
    public List<OrderItem> Items { get; init; } = [];
    public string Status { get; init; } = "Draft";
    public Discount? Discount { get; init; }
}

public record OrderItem
{
    public int ProductId { get; init; }
    public required string Name { get; init; }
    public decimal Price { get; init; }
    public int Quantity { get; init; }
}

public record Customer
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
    public Address? Address { get; init; }
}

public record Address
{
    public required string City { get; init; }
    public required string Country { get; init; }
}

public record Discount
{
    public required string Code { get; init; }
    public decimal Percentage { get; init; }
}

public record Product
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public int Stock { get; init; }
    public required string Category { get; init; }
}

public record ShippingInfo(int OrderId, string CustomerName, string City, string Country, int EstimatedDays);
public record OrderItemDto(int ProductId, string Name, decimal Price, int Quantity);
public record DiscountResult(int OrderId, decimal Subtotal, decimal DiscountAmount, decimal EffectiveRate);
public record StockInfo(int ProductId, string ProductName, int CurrentStock, string Status);
public record CustomerStats(int CustomerId, int TotalOrders, decimal FirstOrderValue, decimal LastOrderValue, decimal AverageOrderValue);