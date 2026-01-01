targetScope = 'subscription'

@description('Name of the environment (e.g., dev, prod)')
param environmentName string

@description('Primary location for all resources')
param location string

@description('Anthropic API Key')
@secure()
param anthropicApiKey string = ''

@description('GitHub Token')
@secure()
param githubToken string = ''

@description('GitHub Owner')
param githubOwner string = ''

@description('GitHub Repository')
param githubRepo string = ''

@description('Application Insights Workspace ID')
param appInsightsWorkspaceId string = ''

var abbrs = loadJsonContent('abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = {
  'azd-env-name': environmentName
  'application': 'ai-diagnostics-agent'
}

// Resource Group
resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

// Log Analytics Workspace
module logAnalytics 'core/monitor/loganalytics.bicep' = {
  name: 'loganalytics'
  scope: rg
  params: {
    name: '${abbrs.operationalInsightsWorkspaces}${resourceToken}'
    location: location
    tags: tags
  }
}

// Application Insights
module appInsights 'core/monitor/applicationinsights.bicep' = {
  name: 'appinsights'
  scope: rg
  params: {
    name: '${abbrs.insightsComponents}${resourceToken}'
    location: location
    tags: tags
    logAnalyticsWorkspaceId: logAnalytics.outputs.id
  }
}

// Container Apps Environment
module containerAppsEnvironment 'core/host/container-apps-environment.bicep' = {
  name: 'container-apps-environment'
  scope: rg
  params: {
    name: '${abbrs.appManagedEnvironments}${resourceToken}'
    location: location
    tags: tags
    logAnalyticsWorkspaceId: logAnalytics.outputs.id
  }
}

// Container Registry
module containerRegistry 'core/host/container-registry.bicep' = {
  name: 'container-registry'
  scope: rg
  params: {
    name: '${abbrs.containerRegistryRegistries}${resourceToken}'
    location: location
    tags: tags
  }
}

// Agent API Container App
module agentApi 'core/host/container-app.bicep' = {
  name: 'agent-api'
  scope: rg
  params: {
    name: 'agent-api'
    location: location
    tags: union(tags, { 'azd-service-name': 'agent-api' })
    containerAppsEnvironmentName: containerAppsEnvironment.outputs.name
    containerRegistryName: containerRegistry.outputs.name
    containerName: 'agent-api'
    targetPort: 8080
    env: [
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.outputs.connectionString }
      { name: 'Agent__Llm__Provider', value: 'Anthropic' }
      { name: 'Agent__Llm__Anthropic__ApiKey', secretRef: 'anthropic-api-key' }
      { name: 'Agent__Llm__Anthropic__Model', value: 'claude-sonnet-4-5-20250929' }
      { name: 'Agent__GitHub__Token', secretRef: 'github-token' }
      { name: 'Agent__GitHub__Owner', value: githubOwner }
      { name: 'Agent__GitHub__Repo', value: githubRepo }
      { name: 'Agent__AppInsights__WorkspaceId', value: appInsightsWorkspaceId }
    ]
    secrets: [
      { name: 'anthropic-api-key', value: anthropicApiKey }
      { name: 'github-token', value: githubToken }
    ]
  }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = containerRegistry.outputs.loginServer
output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = containerAppsEnvironment.outputs.name
output APPLICATIONINSIGHTS_CONNECTION_STRING string = appInsights.outputs.connectionString
output AGENT_API_URL string = agentApi.outputs.uri
