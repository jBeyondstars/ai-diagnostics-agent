# Deploy AI Diagnostics Agent to Azure Container Apps
# Usage: .\deploy.ps1 -ResourceGroup "my-rg" -Location "westeurope"

param(
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroup = "rg-ai-diagnostics-agent",

    [Parameter(Mandatory=$false)]
    [string]$Location = "westeurope",

    [Parameter(Mandatory=$false)]
    [string]$Environment = "dev"
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "AI Diagnostics Agent - Azure Deployment" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check prerequisites
Write-Host "Checking prerequisites..." -ForegroundColor Yellow
if (-not (Get-Command azd -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: Azure Developer CLI (azd) not found. Install from https://aka.ms/azd" -ForegroundColor Red
    exit 1
}
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: Azure CLI (az) not found. Install from https://aka.ms/installazurecli" -ForegroundColor Red
    exit 1
}

# Login check
Write-Host "Checking Azure login..." -ForegroundColor Yellow
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Host "Not logged in. Initiating login..." -ForegroundColor Yellow
    az login
    azd auth login
}
Write-Host "Logged in as: $($account.user.name)" -ForegroundColor Green

# Get secrets from user-secrets
Write-Host ""
Write-Host "Reading local user-secrets..." -ForegroundColor Yellow
$secrets = dotnet user-secrets list --project src/Agent.Api 2>$null

$anthropicKey = ($secrets | Where-Object { $_ -match "Anthropic:ApiKey" }) -replace ".*= ", ""
$githubToken = ($secrets | Where-Object { $_ -match "GitHub:Token" }) -replace ".*= ", ""
$githubOwner = ($secrets | Where-Object { $_ -match "GitHub:Owner" }) -replace ".*= ", ""
$githubRepo = ($secrets | Where-Object { $_ -match "GitHub:Repo" }) -replace ".*= ", ""
$workspaceId = ($secrets | Where-Object { $_ -match "WorkspaceId" }) -replace ".*= ", ""
$appInsightsConn = ($secrets | Where-Object { $_ -match "ConnectionString" }) -replace ".*= ", ""

if (-not $anthropicKey -or -not $githubToken) {
    Write-Host "ERROR: Missing required secrets. Configure with:" -ForegroundColor Red
    Write-Host '  dotnet user-secrets set "Agent:Llm:Anthropic:ApiKey" "sk-ant-..."' -ForegroundColor Gray
    Write-Host '  dotnet user-secrets set "Agent:GitHub:Token" "github_pat_..."' -ForegroundColor Gray
    exit 1
}

Write-Host "Secrets loaded successfully" -ForegroundColor Green

# Deploy with azd
Write-Host ""
Write-Host "Deploying to Azure..." -ForegroundColor Yellow
Write-Host "  Resource Group: $ResourceGroup" -ForegroundColor Gray
Write-Host "  Location: $Location" -ForegroundColor Gray
Write-Host ""

# Set environment
azd env new $Environment 2>$null
azd env set AZURE_LOCATION $Location
azd env set AZURE_RESOURCE_GROUP $ResourceGroup

# Deploy
azd up --no-prompt

# Get the deployed URL
Write-Host ""
Write-Host "Getting deployed URL..." -ForegroundColor Yellow
$apiUrl = azd env get-values | Where-Object { $_ -match "AZURE_CONTAINER_APPS_AGENT_API_URL" } | ForEach-Object { $_ -replace ".*=", "" -replace '"', '' }

if (-not $apiUrl) {
    # Try to get from Azure directly
    $apiUrl = az containerapp show --name "agent-api" --resource-group $ResourceGroup --query "properties.configuration.ingress.fqdn" -o tsv 2>$null
    if ($apiUrl) { $apiUrl = "https://$apiUrl" }
}

if ($apiUrl) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "Deployment Complete!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "API URL: $apiUrl" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Endpoints:" -ForegroundColor Yellow
    Write-Host "  Swagger UI:    $apiUrl/scalar/v1" -ForegroundColor Gray
    Write-Host "  Analyze:       $apiUrl/api/diagnostics/analyze-latest" -ForegroundColor Gray
    Write-Host "  Webhook:       $apiUrl/api/diagnostics/webhook/alert" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Yellow
    Write-Host "  1. Configure secrets in Container App:" -ForegroundColor Gray
    Write-Host "     az containerapp secret set --name agent-api --resource-group $ResourceGroup \" -ForegroundColor Gray
    Write-Host "       --secrets anthropic-key=`"$anthropicKey`" github-token=`"$githubToken`"" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  2. Set environment variables:" -ForegroundColor Gray
    Write-Host "     az containerapp update --name agent-api --resource-group $ResourceGroup \" -ForegroundColor Gray
    Write-Host "       --set-env-vars `"Agent__Llm__Anthropic__ApiKey=secretref:anthropic-key`" \" -ForegroundColor Gray
    Write-Host "       `"Agent__GitHub__Token=secretref:github-token`" \" -ForegroundColor Gray
    Write-Host "       `"Agent__GitHub__Owner=$githubOwner`" \" -ForegroundColor Gray
    Write-Host "       `"Agent__GitHub__Repo=$githubRepo`"" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  3. Deploy Alert Rule:" -ForegroundColor Gray
    Write-Host "     Update infra/alert-rule.parameters.json with webhookUrl: $apiUrl/api/diagnostics/webhook/alert" -ForegroundColor Gray
    Write-Host "     az deployment group create --resource-group $ResourceGroup --template-file infra/alert-rule.bicep --parameters @infra/alert-rule.parameters.json" -ForegroundColor Gray
} else {
    Write-Host "Could not determine API URL. Check Azure Portal for the Container App URL." -ForegroundColor Yellow
}
