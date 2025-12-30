var builder = DistributedApplication.CreateBuilder(args);

// ============================================================================
// Configuration Parameters (secrets) - Aspire 9.1 syntax
// ============================================================================

var llmProvider = builder.AddParameter("LlmProvider", secret: false);
var openAiKey = builder.AddParameter("OpenAiApiKey", secret: true);
var azureOpenAiEndpoint = builder.AddParameter("AzureOpenAiEndpoint", secret: false);
var azureOpenAiKey = builder.AddParameter("AzureOpenAiKey", secret: true);

var appInsightsWorkspaceId = builder.AddParameter("AppInsightsWorkspaceId", secret: false);

var gitHubToken = builder.AddParameter("GitHubToken", secret: true);
var gitHubOwner = builder.AddParameter("GitHubOwner", secret: false);
var gitHubRepo = builder.AddParameter("GitHubRepo", secret: false);

// ============================================================================
// Services - Aspire 9.1 with improved resource relationships
// ============================================================================

var api = builder.AddProject<Projects.Agent_Api>("agent-api")
    .WithEnvironment("Agent__Llm__Provider", llmProvider)
    .WithEnvironment("Agent__Llm__OpenAI__ApiKey", openAiKey)
    .WithEnvironment("Agent__Llm__AzureOpenAI__Endpoint", azureOpenAiEndpoint)
    .WithEnvironment("Agent__Llm__AzureOpenAI__ApiKey", azureOpenAiKey)
    .WithEnvironment("Agent__AppInsights__WorkspaceId", appInsightsWorkspaceId)
    .WithEnvironment("Agent__GitHub__Token", gitHubToken)
    .WithEnvironment("Agent__GitHub__Owner", gitHubOwner)
    .WithEnvironment("Agent__GitHub__Repo", gitHubRepo)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReplicas(1);

var worker = builder.AddProject<Projects.Agent_Worker>("agent-worker")
    .WithEnvironment("Agent__Llm__Provider", llmProvider)
    .WithEnvironment("Agent__Llm__OpenAI__ApiKey", openAiKey)
    .WithEnvironment("Agent__Llm__AzureOpenAI__Endpoint", azureOpenAiEndpoint)
    .WithEnvironment("Agent__Llm__AzureOpenAI__ApiKey", azureOpenAiKey)
    .WithEnvironment("Agent__AppInsights__WorkspaceId", appInsightsWorkspaceId)
    .WithEnvironment("Agent__GitHub__Token", gitHubToken)
    .WithEnvironment("Agent__GitHub__Owner", gitHubOwner)
    .WithEnvironment("Agent__GitHub__Repo", gitHubRepo)
    .WithReplicas(1);

builder.Build().Run();
