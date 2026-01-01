# AI Diagnostics Agent

> An AI-powered agent that monitors Application Insights, detects exceptions, analyzes your code, and automatically creates Pull Requests with fixes.

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    SELF-HEALING LOOP                                          │
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                              │
│   ┌──────────────┐    ┌───────────────┐    ┌─────────────┐    ┌─────────────────────┐       │
│   │  Your App    │    │  Application  │    │    Log      │    │   Azure Monitor     │       │
│   │  (throws     │───▶│   Insights    │───▶│  Analytics  │───▶│   Alert Rule        │       │
│   │  exception)  │    │               │    │  Workspace  │    │   (KQL query)       │       │
│   └──────────────┘    └───────────────┘    └─────────────┘    └──────────┬──────────┘       │
│                                                                          │                   │
│   ┌──────────────────────────────────────────────────────────────────────┘                   │
│   │ Webhook trigger                                                                          │
│   ▼                                                                                          │
│   ┌──────────────┐    ┌───────────────┐    ┌─────────────┐    ┌─────────────────────┐       │
│   │   Action     │    │  AI Agent     │    │  Exception  │    │   Claude            │       │
│   │   Group      │───▶│   API         │───▶│  Filter     │───▶│(or your LLM choice)       │
│   │  (webhook)   │    │  (Container)  │    │             │    │     (analyze)       │       │
│   └──────────────┘    └───────────────┘    └──────┬──────┘    └──────────┬──────────┘       │
│                              │                    │                      │                   │
│                              ▼                    │ Skip infra           ▼                   │
│                       ┌─────────────┐             │ exceptions    ┌─────────────────────┐   │
│                       │    Redis    │             │               │    GitHub           │   │
│                       │   (cache)   │◀────────────┘               │    Pull Request     │   │
│                       └─────────────┘                             └─────────────────────┘   │
│                                                                                              │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

## How It Works

The agent works in **3 phases** with only **1 LLM call**:

| Phase | Description | LLM Calls |
|-------|-------------|-----------|
| **Phase 1: Prefetch** | Fetch exceptions from Log Analytics + source code from GitHub | 0 |
| **Phase 2: Analyze** | Analyze with Claude - propose fixes with exact code snippets | 1 |
| **Phase 3: Create PR** | Apply fixes and create Pull Request on GitHub | 0 |

### Automated Trigger Flow

1. **Exception occurs** in your application
2. **Application Insights** logs the exception to Log Analytics
3. **Azure Monitor Alert Rule** detects the exception (KQL query every 5 min)
4. **Action Group** triggers webhook to the AI Agent API
5. **Redis deduplication** checks if this exception was recently analyzed
6. **Exception filtering** removes non-actionable exceptions (infrastructure/transient errors)
7. **Agent queries Log Analytics** for exception details (Managed Identity)
8. **Agent fetches source code** from GitHub based on stack traces
9. **Claude analyzes** and proposes fixes
10. **Agent creates PR** with the proposed fixes

## Project Structure

```
src/
├── Agent.Core/              # Core agent logic
│   ├── DiagnosticsAgent.cs  # Main 3-phase orchestration
│   ├── Services/
│   │   └── DeduplicationService.cs  # Redis-based deduplication
│   ├── Models/              # Data models
│   └── Configuration/       # Settings classes
├── Agent.Plugins/           # Semantic Kernel plugins
│   ├── AppInsightsPlugin.cs # Query Log Analytics (Managed Identity)
│   └── GitHubPlugin.cs      # Read code, check existing PRs, create PRs
├── Agent.Api/               # HTTP API (webhook endpoints)
│   ├── Controllers/
│   │   └── DiagnosticsController.cs  # Webhook + manual triggers
│   └── Middleware/
└── Agent.ServiceDefaults/   # Shared configuration

infra/
├── main.bicep               # Main infrastructure
├── alert-rule.bicep         # Azure Monitor Alert Rule + Action Group
└── core/                    # Reusable Bicep modules
```

## Quick Start

### Prerequisites

- .NET 10.0 SDK
- Anthropic API key (Claude Sonnet 4 recommended)
- Azure subscription with Application Insights
- GitHub Personal Access Token (Fine-grained, with Contents + Pull requests permissions)

### 1. Clone and Configure

```bash
git clone https://github.com/yourusername/ai-diagnostics-agent.git
cd ai-diagnostics-agent
```

### 2. Set Your API Keys

Use .NET User Secrets (recommended for local development):

```bash
cd src/Agent.Api

# Anthropic Claude
dotnet user-secrets set "Agent:Llm:Anthropic:ApiKey" "sk-ant-api03-..."
dotnet user-secrets set "Agent:Llm:Anthropic:Model" "claude-sonnet-4-5-20250929"

# Application Insights (Log Analytics Workspace Customer ID)
dotnet user-secrets set "Agent:AppInsights:WorkspaceId" "your-log-analytics-customer-id"
dotnet user-secrets set "ApplicationInsights:ConnectionString" "InstrumentationKey=..."

# GitHub (Fine-grained PAT with Contents + Pull requests permissions)
dotnet user-secrets set "Agent:GitHub:Token" "github_pat_..."
dotnet user-secrets set "Agent:GitHub:Owner" "your-org"
dotnet user-secrets set "Agent:GitHub:Repo" "your-repo"

# Redis (optional, for deduplication)
dotnet user-secrets set "Agent:Redis:ConnectionString" "localhost:6379"
```

### 3. Run Locally

```bash
cd src/Agent.Api
dotnet run
```

### 4. Deploy to Azure

```bash
# Initialize Azure Developer CLI
azd init

# Set environment variables
azd env set ANTHROPIC_API_KEY "sk-ant-..."
azd env set GITHUB_TOKEN "github_pat_..."
azd env set GITHUB_OWNER "your-org"
azd env set GITHUB_REPO "your-repo"

# Deploy
azd up
```

After deployment, configure the Managed Identity:

```bash
# Enable System-Assigned Managed Identity
az containerapp identity assign --name agent-api --resource-group rg-dev --system-assigned

# Get the principal ID
PRINCIPAL_ID=$(az containerapp show --name agent-api --resource-group rg-dev --query "identity.principalId" -o tsv)

# Assign Reader role on Log Analytics Workspace
az role assignment create \
  --role "Reader" \
  --assignee-object-id $PRINCIPAL_ID \
  --scope "/subscriptions/{sub-id}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{workspace-name}"
```

### 5. Deploy Alert Rule

```bash
az deployment group create \
  --resource-group rg-dev \
  --template-file infra/alert-rule.bicep \
  --parameters \
    webhookUrl="https://your-agent.azurecontainerapps.io/api/diagnostics/webhook/alert" \
    appInsightsId="/subscriptions/.../microsoft.insights/components/your-app-insights" \
    logAnalyticsId="/subscriptions/.../Microsoft.OperationalInsights/workspaces/your-workspace"
```

## API Endpoints

### Webhook (Azure Monitor)

```bash
# Called automatically by Azure Monitor Action Group
POST /api/diagnostics/webhook/alert
Content-Type: application/json
# Body: Azure Monitor Common Alert Schema
```

### Manual Trigger

```bash
# Trigger analysis manually
curl -X POST "https://your-agent/api/diagnostics/webhook/trigger?createPR=true"

# Dry run (no PR)
curl -X POST "https://your-agent/api/diagnostics/webhook/trigger?createPR=false"
```

### Analyze Latest Exception

```bash
# Analyze the most recent exception
curl -X POST "https://your-agent/api/diagnostics/analyze-latest?createPR=true"
```

### Test Connection

```bash
# Verify App Insights and GitHub connections
curl "https://your-agent/api/diagnostics/test-appinsights"
```

### Test Endpoints (Generate Exceptions)

The API includes test endpoints to generate various exceptions for testing:

```bash
# Fixable exceptions (Claude will propose PRs)
GET /api/orders/customer/999/average     # DivideByZeroException (no orders for customer)
GET /api/orders/parse-date?dateString=x  # FormatException (invalid date format)
GET /api/users/3                          # NullReferenceException (null email)
GET /api/users/search?query=xyz           # IndexOutOfRangeException (no results)

# Infrastructure exceptions (filtered out, no PR)
GET /api/testexceptions/timeout           # TimeoutException
GET /api/testexceptions/external-api      # HttpRequestException
```

## Deduplication

The agent uses Redis (or in-memory cache for local dev) to prevent analyzing the same exception multiple times:

- **Exception cooldown**: 6 hours by default
- **Webhook debounce**: 5 minutes per alert rule
- **Existing PR check**: Skips if an open PR already addresses the exception (by ProblemId + source file)

## Exception Filtering

The agent intelligently filters out non-actionable exceptions to avoid creating useless PRs:

### Level 1: Exclusion List

Infrastructure and transient exceptions are automatically ignored:

```
System.Net.Http.HttpRequestException
System.TimeoutException
System.Threading.Tasks.TaskCanceledException
StackExchange.Redis.RedisConnectionException
Azure.RequestFailedException
Microsoft.Data.SqlClient.SqlException
...
```

### Level 2: Pattern Matching

Partial matches are also filtered:

```
ConnectionException, Timeout, Unreachable, NetworkError, HostNotFound
```

### Level 3: Claude Evaluation

For ambiguous exceptions (containing "api", "service", "500", etc.), Claude evaluates if the exception is fixable by code changes:

- **YES**: Missing error handling, retry logic, input validation → Create PR
- **NO**: External service down, network issues → Skip

Configure in `appsettings.json`:

```json
{
  "Agent": {
    "ExceptionFilter": {
      "ExcludedTypes": ["System.Net.Http.HttpRequestException", "..."],
      "ExcludedPatterns": ["ConnectionException", "Timeout"],
      "EnableClaudeEvaluation": true
    }
  }
}
```

## Configuration

### Environment Variables

| Variable | Description |
|----------|-------------|
| `Agent__Llm__Provider` | `Anthropic` or `AzureOpenAI` |
| `Agent__Llm__Anthropic__ApiKey` | Anthropic API key |
| `Agent__Llm__Anthropic__Model` | Model ID (e.g., `claude-sonnet-4-5-20250929`) |
| `Agent__AppInsights__WorkspaceId` | Log Analytics Workspace Customer ID |
| `Agent__GitHub__Token` | GitHub PAT |
| `Agent__GitHub__Owner` | GitHub organization/user |
| `Agent__GitHub__Repo` | Target repository |
| `Agent__Redis__ConnectionString` | Redis connection string (optional) |
| `Agent__ExceptionFilter__EnableClaudeEvaluation` | Enable Claude to evaluate ambiguous exceptions (default: `true`) |

### GitHub Token Permissions

For **Fine-grained PAT**:
- Repository access: Select your target repository
- **Contents**: Read and Write (create branches, commit files)
- **Pull requests**: Read and Write (create PRs, check existing PRs)

## Cost Estimation

| Component | Cost |
|-----------|------|
| **Claude Sonnet 4** | ~$0.05 per analysis |
| **Azure Alert Rule** | ~$0.10/month |
| **Container App** | ~$20/month (consumption plan) |
| **Redis (optional)** | ~$15/month (Basic tier) |

**Total**: ~$35-50/month for the infrastructure, plus ~$0.05 per exception analyzed.

## Performance

| Metric | Value |
|--------|-------|
| LLM calls per analysis | 1 |
| Typical analysis time | 10-30 seconds |
| Token usage | ~15,000 input, ~500 output |
| PR success rate | ~95% |

## Troubleshooting

### "403 Forbidden" when querying Log Analytics

Ensure the Managed Identity has the `Reader` role on the Log Analytics Workspace resource.

### "Resource not accessible by personal access token"

GitHub token lacks permissions. Ensure **Contents** and **Pull requests** are Read and Write.

### Alert not firing

1. Verify exceptions exist: Run the KQL query manually in Log Analytics
2. Check the alert scope: Must be the Log Analytics Workspace ID, not App Insights
3. Use `AppExceptions` table (not `exceptions`) for workspace-based queries

### Duplicate PRs being created

Enable Redis deduplication or check that the deduplication service is working:
- Check logs for "Already analyzed recently" messages
- Verify Redis connection string is set

## License

MIT License - feel free to use in your projects!

## Contributing

PRs welcome! Please read CONTRIBUTING.md first.
