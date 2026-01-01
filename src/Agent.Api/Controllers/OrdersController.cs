using Microsoft.AspNetCore.Mvc;

namespace Agent.Api.Controllers;

/// <summary>
/// Orders controller with 5 intentional bugs for AI diagnostics testing.
/// Each endpoint contains a real bug that Claude should detect and fix.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class OrdersController(ILogger<OrdersController> logger) : ControllerBase
{
    // Simulated database
    private static readonly Dictionary<int, OrderEntity> _orders = new()
    {
        [1001] = new OrderEntity
        {
            Id = 1001,
            CustomerId = 1,
            Items = [new OrderItem { ProductId = 101, Name = "Laptop", Price = 999.99m, Quantity = 1 }],
            Status = "Completed",
            Discount = null
        },
        [1002] = new OrderEntity
        {
            Id = 1002,
            CustomerId = 2,
            Items = [
                new OrderItem { ProductId = 102, Name = "Mouse", Price = 29.99m, Quantity = 2 },
                new OrderItem { ProductId = 103, Name = "Keyboard", Price = 79.99m, Quantity = 1 }
            ],
            Status = "Pending",
            Discount = new Discount { Code = "SAVE10", Percentage = 10 }
        },
        [1003] = new OrderEntity
        {
            Id = 1003,
            CustomerId = 3,
            Items = [],  // Empty order - will cause bugs
            Status = "Draft",
            Discount = null
        }
    };

    private static readonly Dictionary<int, Customer> _customers = new()
    {
        [1] = new Customer { Id = 1, Name = "Alice Smith", Email = "alice@example.com", Address = new Address { City = "Paris", Country = "France" } },
        [2] = new Customer { Id = 2, Name = "Bob Jones", Email = "bob@example.com", Address = null },  // No address
        [3] = new Customer { Id = 3, Name = "Charlie Brown", Email = "charlie@example.com", Address = new Address { City = "London", Country = "UK" } }
    };

    private static readonly Dictionary<int, Product> _products = new()
    {
        [101] = new Product { Id = 101, Name = "Laptop", Stock = 50, Category = "Electronics" },
        [102] = new Product { Id = 102, Name = "Mouse", Stock = 200, Category = "Electronics" },
        [103] = new Product { Id = 103, Name = "Keyboard", Stock = 0, Category = "Electronics" }
    };

    /// <summary>
    /// BUG 1: NullReferenceException - Customer address can be null
    /// Test: GET /api/orders/1002/shipping
    /// </summary>
    [HttpGet("{orderId:int}/shipping")]
    public ActionResult<ShippingInfo> GetShippingInfo([FromRoute] int orderId)
    {
        logger.LogInformation("Getting shipping info for order {OrderId}", orderId);

        if (!_orders.TryGetValue(orderId, out var order))
        {
            return NotFound(new { message = $"Order {orderId} not found" });
        }

        var customer = _customers[order.CustomerId];

        // BUG: customer.Address can be null (Customer ID 2 has no address)
        var shippingCity = customer.Address.City;
        var shippingCountry = customer.Address.Country;

        logger.LogInformation("Shipping to {City}, {Country}", shippingCity, shippingCountry);

        return Ok(new ShippingInfo
        {
            OrderId = orderId,
            CustomerName = customer.Name,
            City = shippingCity,
            Country = shippingCountry,
            EstimatedDays = shippingCountry == "France" ? 2 : 5
        });
    }

    /// <summary>
    /// BUG 2: InvalidOperationException - Empty collection with .First()
    /// Test: GET /api/orders/1003/first-item
    /// </summary>
    [HttpGet("{orderId:int}/first-item")]
    public ActionResult<OrderItemDto> GetFirstItem([FromRoute] int orderId)
    {
        logger.LogInformation("Getting first item for order {OrderId}", orderId);

        if (!_orders.TryGetValue(orderId, out var order))
        {
            return NotFound(new { message = $"Order {orderId} not found" });
        }

        // BUG: Order 1003 has empty Items list - .First() will throw InvalidOperationException
        var firstItem = order.Items.First();

        return Ok(new OrderItemDto
        {
            ProductId = firstItem.ProductId,
            Name = firstItem.Name,
            Price = firstItem.Price,
            Quantity = firstItem.Quantity
        });
    }

    /// <summary>
    /// BUG 3: DivideByZeroException - Division when total is zero
    /// Test: GET /api/orders/1003/discount-rate
    /// </summary>
    [HttpGet("{orderId:int}/discount-rate")]
    public ActionResult<DiscountResult> CalculateDiscountRate([FromRoute] int orderId)
    {
        logger.LogInformation("Calculating discount rate for order {OrderId}", orderId);

        if (!_orders.TryGetValue(orderId, out var order))
        {
            return NotFound(new { message = $"Order {orderId} not found" });
        }

        var subtotal = order.Items.Sum(i => i.Price * i.Quantity);
        var discountAmount = order.Discount?.Percentage ?? 0;

        // Handle zero subtotal to prevent division by zero
        var effectiveRate = subtotal > 0 
            ? (discountAmount * 100) / subtotal 
            : 0m;

        logger.LogInformation("Discount rate: {Rate}%", effectiveRate);

        return Ok(new DiscountResult
        {
            OrderId = orderId,
            Subtotal = subtotal,
            DiscountAmount = discountAmount,
            EffectiveRate = effectiveRate
        });
    }

    /// <summary>
    /// BUG 4: KeyNotFoundException - Product ID doesn't exist in dictionary
    /// Test: GET /api/orders/products/999/stock
    /// </summary>
    [HttpGet("products/{productId:int}/stock")]
    public ActionResult<StockInfo> GetProductStock([FromRoute] int productId)
    {
        logger.LogInformation("Checking stock for product {ProductId}", productId);

        // BUG: No TryGetValue - throws KeyNotFoundException for non-existent products
        var product = _products[productId];

        var stockStatus = product.Stock switch
        {
            0 => "Out of Stock",
            < 10 => "Low Stock",
            < 50 => "In Stock",
            _ => "Well Stocked"
        };

        return Ok(new StockInfo
        {
            ProductId = productId,
            ProductName = product.Name,
            CurrentStock = product.Stock,
            Status = stockStatus
        });
    }

    /// <summary>
    /// BUG 5: IndexOutOfRangeException - Accessing array with invalid index
    /// Test: GET /api/orders/customer/99/average
    /// </summary>
    [HttpGet("customer/{customerId:int}/average")]
    public ActionResult<CustomerStats> GetCustomerAverageOrderValue([FromRoute] int customerId)
    {
        logger.LogInformation("Calculating average order value for customer {CustomerId}", customerId);

        var customerOrders = _orders.Values
            .Where(o => o.CustomerId == customerId)
            .ToList();

        // BUG: If customer has no orders (e.g., customerId=99), this array is empty
        var orderTotals = customerOrders
            .Select(o => o.Items.Sum(i => i.Price * i.Quantity))
            .ToArray();

        // BUG: Accessing index 0 on empty array throws IndexOutOfRangeException
        var firstOrderTotal = orderTotals[0];
        var lastOrderTotal = orderTotals[orderTotals.Length - 1];
        var averageTotal = orderTotals.Sum() / orderTotals.Length;

        logger.LogInformation("Customer {CustomerId}: First={First}, Last={Last}, Avg={Avg}",
            customerId, firstOrderTotal, lastOrderTotal, averageTotal);

        return Ok(new CustomerStats
        {
            CustomerId = customerId,
            TotalOrders = customerOrders.Count,
            FirstOrderValue = firstOrderTotal,
            LastOrderValue = lastOrderTotal,
            AverageOrderValue = averageTotal
        });
    }
}

#region Models

public class OrderEntity
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    public string Status { get; set; } = "Draft";
    public Discount? Discount { get; set; }
}

public class OrderItem
{
    public int ProductId { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public Address? Address { get; set; }
}

public class Address
{
    public required string City { get; set; }
    public required string Country { get; set; }
}

public class Discount
{
    public required string Code { get; set; }
    public decimal Percentage { get; set; }
}

public class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int Stock { get; set; }
    public required string Category { get; set; }
}

#endregion

#region DTOs

public class ShippingInfo
{
    public int OrderId { get; set; }
    public required string CustomerName { get; set; }
    public required string City { get; set; }
    public required string Country { get; set; }
    public int EstimatedDays { get; set; }
}

public class OrderItemDto
{
    public int ProductId { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}

public class DiscountResult
{
    public int OrderId { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal EffectiveRate { get; set; }
}

public class StockInfo
{
    public int ProductId { get; set; }
    public required string ProductName { get; set; }
    public int CurrentStock { get; set; }
    public required string Status { get; set; }
}

public class CustomerStats
{
    public int CustomerId { get; set; }
    public int TotalOrders { get; set; }
    public decimal FirstOrderValue { get; set; }
    public decimal LastOrderValue { get; set; }
    public decimal AverageOrderValue { get; set; }
}

#endregion
