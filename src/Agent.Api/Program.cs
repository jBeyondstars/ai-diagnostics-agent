using Agent.Api.Middleware;
using Agent.Api.Services;
using Agent.Core;
using Agent.Core.Configuration;
using Agent.Core.Services;
using Microsoft.AspNetCore.HttpOverrides;

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
builder.Services.AddSingleton<PricingService>();
builder.Services.AddSingleton<InventoryService>();

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

builder.Services.AddProblemDetails();

// Configure forwarded headers for Azure Container Apps reverse proxy
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// CORS for Swagger UI
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

// Handle forwarded headers from Azure Container Apps reverse proxy (configured in services)
app.UseForwardedHeaders();

app.UseGlobalExceptionHandler();

app.UseCors();

app.MapOpenApi().RequireCors();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "AI Diagnostics Agent API v1");
    options.RoutePrefix = "swagger";
    options.DocumentTitle = "AI Diagnostics Agent";
});

app.MapDefaultEndpoints();

app.MapControllers();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/")
    {
        context.Response.Redirect("/swagger");
        return;
    }
    await next();
});

app.Run();
