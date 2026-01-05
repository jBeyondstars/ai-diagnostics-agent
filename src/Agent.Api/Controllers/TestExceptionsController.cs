using Microsoft.AspNetCore.Mvc;

namespace Agent.Api.Controllers;

/// <summary>
/// Test controller to generate various exceptions for Application Insights testing.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class TestExceptionsController(ILogger<TestExceptionsController> logger) : ControllerBase
{
    [HttpGet("null-reference")]
    public ActionResult ThrowNullReference()
    {
        logger.LogInformation("About to throw NullReferenceException");
        string? nullString = null;
        return Ok(nullString!.Length);
    }

    [HttpGet("argument")]
    public ActionResult ThrowArgumentException([FromQuery] string? value)
    {
        logger.LogInformation("Validating argument: {Value}", value);
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("Value cannot be null or empty", nameof(value));
        return Ok(new { value });
    }

    [HttpGet("invalid-operation")]
    public ActionResult ThrowInvalidOperation()
    {
        logger.LogWarning("About to perform invalid operation");
        throw new InvalidOperationException("Cannot process order: Customer account is in invalid state.");
    }

    [HttpGet("business-error")]
    public ActionResult ThrowBusinessException([FromQuery] int orderId = 12345)
    {
        logger.LogInformation("Processing order {OrderId}", orderId);
        throw new OrderProcessingException(orderId, "Insufficient inventory for requested items");
    }

    [HttpGet("timeout")]
    public async Task<ActionResult> ThrowTimeout(CancellationToken ct)
    {
        logger.LogInformation("Starting long-running operation");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Database query timed out after 100ms");
        }
        return Ok();
    }

    [HttpGet("external-api")]
    public ActionResult ThrowHttpException()
    {
        logger.LogInformation("Calling external payment API");
        throw new HttpRequestException(
            "Failed to connect to payment gateway",
            inner: null,
            statusCode: System.Net.HttpStatusCode.ServiceUnavailable);
    }

    [HttpGet("divide-by-zero")]
    public ActionResult ThrowDivideByZero([FromQuery] int numerator = 100)
    {
        logger.LogInformation("Calculating ratio with numerator {Numerator}", numerator);
        return Ok(numerator / 0);
    }

    [HttpGet("file-not-found")]
    public ActionResult ThrowFileNotFound()
    {
        logger.LogInformation("Reading configuration file");
        throw new FileNotFoundException("Configuration file not found", "/etc/app/config.json");
    }

    [HttpGet("aggregate")]
    public ActionResult ThrowAggregateException()
    {
        logger.LogInformation("Running parallel operations");
        throw new AggregateException("Multiple parallel operations failed",
        [
            new InvalidOperationException("Task 1 failed: Database connection lost"),
            new TimeoutException("Task 2 failed: Redis cache timeout"),
            new HttpRequestException("Task 3 failed: External API unreachable")
        ]);
    }

    [HttpGet("random")]
    public ActionResult ThrowRandom()
    {
        var random = Random.Shared.Next(5);
        logger.LogInformation("Throwing random exception type {Type}", random);
        throw random switch
        {
            0 => new NullReferenceException("Object reference not set to an instance of an object"),
            1 => new ArgumentException("Invalid argument provided"),
            2 => new InvalidOperationException("Operation is not valid in current state"),
            3 => new TimeoutException("Operation timed out"),
            _ => new Exception("Generic unexpected error occurred")
        };
    }

    [HttpPost("burst")]
    public async Task<ActionResult> GenerateBurst([FromQuery] int count = 10)
    {
        logger.LogInformation("Generating burst of {Count} exceptions", count);
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await Task.Delay(Random.Shared.Next(10, 100));
            try
            {
                throw new Exception($"Burst exception #{i}: Simulated error for testing");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Burst exception {Index} of {Total}", i + 1, count);
            }
        });
        await Task.WhenAll(tasks);
        return Ok(new { message = $"Generated {count} exceptions", timestamp = DateTime.UtcNow });
    }
}

public class OrderProcessingException(int orderId, string reason)
    : Exception($"Failed to process order {orderId}: {reason}")
{
    public int OrderId { get; } = orderId;
    public string Reason { get; } = reason;
}

[ApiController]
[Route("api/[controller]")]
public class UsersController(ILogger<UsersController> logger) : ControllerBase
{
    private static readonly Dictionary<int, User> _users = new()
    {
        [1] = new User { Id = 1, Name = "Alice", Email = "alice@example.com", Roles = ["admin", "user"] },
        [2] = new User { Id = 2, Name = "Bob", Email = "bob@example.com", Roles = ["user"] },
        [3] = new User { Id = 3, Name = "Charlie", Email = null, Roles = [] }
    };

    [HttpGet("{id}")]
    public ActionResult<UserDto> GetUser(int id)
    {
        logger.LogInformation("Fetching user {UserId}", id);
        
        // Demo Bugs: No existence check, no null check, empty roles check
        var user = _users[id];
        return Ok(new UserDto
        {
            Id = user.Id,
            Name = user.Name,
            Email = user.Email!.ToUpper(), 
            PrimaryRole = user.Roles[0]
        });
    }

    [HttpGet("search")]
    public ActionResult<List<UserDto>> SearchUsers([FromQuery] string query)
    {
        logger.LogInformation("Searching users with query: {Query}", query);

        List<User> results = [.. _users.Values.Where(u => u.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];

        // Demo Bug: Assumes results not empty
        logger.LogInformation("First match: {Name}", results[0].Name);

        return Ok(results.Select(u => new UserDto
        {
            Id = u.Id,
            Name = u.Name,
            Email = u.Email ?? "N/A",
            PrimaryRole = u.Roles.FirstOrDefault() ?? "none"
        }).ToList());
    }
}

public record User
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string? Email { get; init; }
    public string[] Roles { get; init; } = [];
}

public record UserDto
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
    public required string PrimaryRole { get; init; }
}