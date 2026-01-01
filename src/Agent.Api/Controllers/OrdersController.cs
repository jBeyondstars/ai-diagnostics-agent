using Microsoft.AspNetCore.Mvc;

namespace Agent.Api.Controllers;

/// <summary>
/// Controller simulating an order service with fixable bugs.
/// These bugs can be identified and fixed by Claude.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class OrdersController(ILogger<OrdersController> logger) : ControllerBase
{
    // Simulated orders database
    private static readonly List<Order> _orders =
    [
        new Order { Id = 1, CustomerId = 1, Total = 99.99m, Status = "Completed", Items = ["Widget", "Gadget"] },
        new Order { Id = 2, CustomerId = 2, Total = 0m, Status = "Pending", Items = [] },  // Bug: empty items
        new Order { Id = 3, CustomerId = 1, Total = 150.00m, Status = "Shipped", Items = ["Laptop"] }
    ];

    /// <summary>
    /// Calculate average order value - has a division by zero bug when no orders exist for customer.
    /// BUG: Does not check if customer has orders before calculating average.
    /// FIX: Add check for empty collection before dividing.
    /// </summary>
    [HttpGet("customer/{customerId}/average")]
    public ActionResult<decimal> GetCustomerAverageOrderValue(int customerId)
    {
        logger.LogInformation("Calculating average order value for customer {CustomerId}", customerId);

        var customerOrders = _orders.Where(o => o.CustomerId == customerId).ToList();

        // BUG: Division by zero when customer has no orders
        var totalValue = customerOrders.Sum(o => o.Total);
        var averageValue = totalValue / customerOrders.Count;

        logger.LogInformation("Customer {CustomerId} average order: {Average}", customerId, averageValue);

        return Ok(averageValue);
    }

    /// <summary>
    /// Parse order date from string - has FormatException bug with invalid date strings.
    /// BUG: Does not validate date format before parsing.
    /// FIX: Use DateTime.TryParse or add proper validation.
    /// </summary>
    [HttpGet("parse-date")]
    public ActionResult<OrderDateInfo> ParseOrderDate([FromQuery] string dateString)
    {
        logger.LogInformation("Parsing order date: {DateString}", dateString);

        // Validate input and use TryParse to handle invalid formats gracefully
        if (string.IsNullOrWhiteSpace(dateString))
        {
            return BadRequest(new { error = "Date string cannot be null or empty" });
        }

        if (!DateTime.TryParse(dateString, out var parsedDate))
        {
            return BadRequest(new { error = $"Invalid date format: '{dateString}'" });
        }

        var dayOfWeek = parsedDate.DayOfWeek;
        var isWeekend = dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday;

        // Safe to access Length here since we validated dateString is not null above
        var inputLength = dateString.Length;

        return Ok(new OrderDateInfo
        {
            OriginalInput = dateString,
            ParsedDate = parsedDate,
            DayOfWeek = dayOfWeek.ToString(),
            IsWeekend = isWeekend,
            InputLength = inputLength
        });
    }
}

public class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public decimal Total { get; set; }
    public required string Status { get; set; }
    public string[] Items { get; set; } = [];
}

public class OrderDateInfo
{
    public required string OriginalInput { get; set; }
    public DateTime ParsedDate { get; set; }
    public required string DayOfWeek { get; set; }
    public bool IsWeekend { get; set; }
    public int InputLength { get; set; }
}
