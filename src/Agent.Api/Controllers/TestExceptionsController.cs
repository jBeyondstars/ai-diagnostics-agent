using Microsoft.AspNetCore.Mvc;

namespace Agent.Api.Controllers;

/// <summary>
/// Test controller to generate various exceptions for Application Insights testing.
/// These endpoints intentionally throw exceptions to verify monitoring setup.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class TestExceptionsController(ILogger<TestExceptionsController> logger) : ControllerBase
{
    /// <summary>
    /// Throws a NullReferenceException for testing.
    /// </summary>
    [HttpGet("null-reference")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult ThrowNullReference()
    {
        logger.LogInformation("About to throw NullReferenceException");

        string? nullString = null;
        var length = nullString!.Length; // This will throw

        return Ok(length);
    }

    /// <summary>
    /// Throws an ArgumentException for testing.
    /// </summary>
    [HttpGet("argument")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult ThrowArgumentException([FromQuery] string? value)
    {
        logger.LogInformation("Validating argument: {Value}", value);

        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("Value cannot be null or empty", nameof(value));
        }

        return Ok(new { value });
    }

    /// <summary>
    /// Throws an InvalidOperationException for testing.
    /// </summary>
    [HttpGet("invalid-operation")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult ThrowInvalidOperation()
    {
        logger.LogWarning("About to perform invalid operation");

        throw new InvalidOperationException(
            "Cannot process order: Customer account is in invalid state. " +
            "Expected status 'Active' but found 'Suspended'.");
    }

    /// <summary>
    /// Throws a custom business exception for testing.
    /// </summary>
    [HttpGet("business-error")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult ThrowBusinessException([FromQuery] int orderId = 12345)
    {
        logger.LogInformation("Processing order {OrderId}", orderId);

        throw new OrderProcessingException(orderId, "Insufficient inventory for requested items");
    }

    /// <summary>
    /// Throws a timeout exception for testing.
    /// </summary>
    [HttpGet("timeout")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> ThrowTimeout(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting long-running operation");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Database query timed out after 100ms while fetching customer data");
        }

        return Ok();
    }

    /// <summary>
    /// Throws an HTTP request exception (simulating external API failure).
    /// </summary>
    [HttpGet("external-api")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult ThrowHttpException()
    {
        logger.LogInformation("Calling external payment API");

        throw new HttpRequestException(
            "Failed to connect to payment gateway at https://api.payments.example.com/v2/charge",
            inner: null,
            statusCode: System.Net.HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// Throws a divide by zero exception for testing.
    /// </summary>
    [HttpGet("divide-by-zero")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult ThrowDivideByZero([FromQuery] int numerator = 100)
    {
        logger.LogInformation("Calculating ratio with numerator {Numerator}", numerator);

        var denominator = 0;
        var result = numerator / denominator; // This will throw

        return Ok(result);
    }

    /// <summary>
    /// Throws a file not found exception for testing.
    /// </summary>
    [HttpGet("file-not-found")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult ThrowFileNotFound()
    {
        logger.LogInformation("Reading configuration file");

        throw new FileNotFoundException(
            "Configuration file not found",
            fileName: "/etc/app/config.json");
    }

    /// <summary>
    /// Throws an aggregate exception with multiple inner exceptions.
    /// </summary>
    [HttpGet("aggregate")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public ActionResult ThrowAggregateException()
    {
        logger.LogInformation("Running parallel operations");

        var exceptions = new List<Exception>
        {
            new InvalidOperationException("Task 1 failed: Database connection lost"),
            new TimeoutException("Task 2 failed: Redis cache timeout"),
            new HttpRequestException("Task 3 failed: External API unreachable")
        };

        throw new AggregateException("Multiple parallel operations failed", exceptions);
    }

    /// <summary>
    /// Randomly throws one of several exception types.
    /// Useful for generating varied test data.
    /// </summary>
    [HttpGet("random")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
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

    /// <summary>
    /// Generates multiple exceptions in rapid succession.
    /// </summary>
    [HttpPost("burst")]
    [ProducesResponseType(StatusCodes.Status200OK)]
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

/// <summary>
/// Custom exception for business logic errors.
/// </summary>
public class OrderProcessingException(int orderId, string reason)
    : Exception($"Failed to process order {orderId}: {reason}")
{
    public int OrderId { get; } = orderId;
    public string Reason { get; } = reason;
}

/// <summary>
/// Controller simulating a real user service with actual bugs.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class UsersController(ILogger<UsersController> logger) : ControllerBase
{
    // Simulated user database
    private static readonly Dictionary<int, User> _users = new()
    {
        [1] = new User { Id = 1, Name = "Alice", Email = "alice@example.com", Roles = ["admin", "user"] },
        [2] = new User { Id = 2, Name = "Bob", Email = "bob@example.com", Roles = ["user"] },
        [3] = new User { Id = 3, Name = "Charlie", Email = null, Roles = [] }  // Bug: null email
    };

    /// <summary>
    /// Gets a user by ID - has a bug with null email handling
    /// </summary>
    [HttpGet("{id}")]
    public ActionResult<UserDto> GetUser(int id)
    {
        logger.LogInformation("Fetching user {UserId}", id);

        // BUG: No check if user exists
        var user = _users[id];

        // BUG: No null check on Email before calling ToUpper()
        var normalizedEmail = user.Email.ToUpper();

        return Ok(new UserDto
        {
            Id = user.Id,
            Name = user.Name,
            Email = normalizedEmail,
            PrimaryRole = user.Roles[0]  // BUG: No check if Roles is empty
        });
    }

    /// <summary>
    /// Searches users by name - has index out of bounds bug
    /// </summary>
    [HttpGet("search")]
    public ActionResult<List<UserDto>> SearchUsers([FromQuery] string query)
    {
        logger.LogInformation("Searching users with query: {Query}", query);

        var results = _users.Values
            .Where(u => u.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // BUG: Assumes at least one result exists
        var firstResult = results[0];
        logger.LogInformation("First match: {Name}", firstResult.Name);

        return Ok(results.Select(u => new UserDto
        {
            Id = u.Id,
            Name = u.Name,
            Email = u.Email ?? "N/A",
            PrimaryRole = u.Roles.FirstOrDefault() ?? "none"
        }).ToList());
    }
}

public class User
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Email { get; set; }
    public string[] Roles { get; set; } = [];
}

public class UserDto
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public required string PrimaryRole { get; set; }
}
