using Agent.Api.Middleware;
using Agent.Core;
using Agent.Core.Configuration;
using Agent.Core.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<AgentConfiguration>(
    builder.Configuration.GetSection(AgentConfiguration.SectionName));

var redisConnection = builder.Configuration.GetValue<string>("Agent:Redis:ConnectionString");
if (!string.IsNullOrEmpty(redisConnection))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnection;
        options.InstanceName = "ai-diagnostics-agent:";
    });
    builder.Services.AddSingleton<DeduplicationService>();
}
else
{
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSingleton<DeduplicationService>();
}

builder.Services.AddSingleton<DiagnosticsAgent>();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.WriteIndented = true;
    });

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

// CORS for Scalar UI
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseGlobalExceptionHandler();

app.UseCors();

// Temporarily disabled due to .NET 10 preview compatibility issues
// app.MapOpenApi();
// app.MapScalarApiReference(options =>
// {
//     options.Title = "AI Diagnostics Agent";
//     options.Theme = ScalarTheme.BluePlanet;
//     options.DefaultHttpClient = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
//     options.ProxyUrl = null;
// });

app.MapDefaultEndpoints();

app.MapControllers();

// Redirect root to API docs (Scalar disabled due to .NET 10 compatibility)
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/")
    {
        context.Response.Redirect("/api/diagnostics/status");
        return;
    }
    await next();
});

app.Run();
