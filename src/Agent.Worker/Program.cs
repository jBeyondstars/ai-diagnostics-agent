using Agent.Core;
using Agent.Core.Configuration;
using Agent.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Add service defaults (OpenTelemetry, health checks, etc.)
builder.AddServiceDefaults();

// Configuration
builder.Services.Configure<AgentConfiguration>(
    builder.Configuration.GetSection(AgentConfiguration.SectionName));

// Register the agent
builder.Services.AddSingleton<DiagnosticsAgent>();

// Register the background worker
builder.Services.AddHostedService<DiagnosticsWorker>();

var host = builder.Build();
host.Run();
