using Agent.Api.Middleware;
using Agent.Core;
using Agent.Core.Configuration;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// Service Configuration
// ============================================================================

builder.AddServiceDefaults();

builder.Services.Configure<AgentConfiguration>(
    builder.Configuration.GetSection(AgentConfiguration.SectionName));

builder.Services.AddSingleton<DiagnosticsAgent>();

// Add Controllers with JSON options
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.WriteIndented = true;
    });

// OpenAPI configuration
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = "AI Diagnostics Agent API";
        document.Info.Version = "v1";
        document.Info.Description = """
            API to trigger and monitor the AI Diagnostics Agent.
            Built with .NET 10 and Semantic Kernel 1.45+.

            ## Test Endpoints
            Use `/api/testexceptions/*` to generate test exceptions for Application Insights.

            ## Diagnostics Endpoints
            Use `/api/diagnostics/*` for actual agent operations.
            """;
        return Task.CompletedTask;
    });
});

// Add ProblemDetails for consistent error responses
builder.Services.AddProblemDetails();

var app = builder.Build();

// ============================================================================
// Middleware Pipeline
// ============================================================================

// Global exception handler (must be first)
app.UseGlobalExceptionHandler();

// OpenAPI and Scalar UI
app.MapOpenApi();
app.MapScalarApiReference(options =>
{
    options.Title = "AI Diagnostics Agent";
    options.Theme = ScalarTheme.BluePlanet;
    options.DefaultHttpClient = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
});

// Health checks and service defaults
app.MapDefaultEndpoints();

// Map Controllers
app.MapControllers();

// ============================================================================
// Root redirect to Scalar UI
// ============================================================================

app.MapGet("/", () => Results.Redirect("/scalar/v1"))
    .ExcludeFromDescription();

app.Run();
