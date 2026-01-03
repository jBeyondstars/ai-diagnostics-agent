// Azure Monitor Alert Rule for Exception Detection
// This template creates an alert that triggers when exceptions are detected in Application Insights

@description('Name of the alert rule')
param alertRuleName string = 'ai-agent-exception-alert'

@description('Application Insights resource ID')
param appInsightsId string

@description('Log Analytics Workspace resource ID')
param logAnalyticsId string = ''

@description('Base URL of the AI Diagnostics Agent (e.g., https://myapp.azurecontainerapps.io)')
param agentBaseUrl string

@description('Analysis mode: "standard" (fast, no tools) or "hybrid" (Claude can use tools if needed)')
@allowed([
  'standard'
  'hybrid'
])
param analysisMode string = 'standard'

@description('Minimum number of exceptions to trigger alert')
param exceptionThreshold int = 1

@description('Location for the resources')
param location string = resourceGroup().location

// Construct webhook URL based on analysis mode
var webhookEndpoint = analysisMode == 'hybrid'
  ? '/api/diagnostics/webhook/alert-hybrid'
  : '/api/diagnostics/webhook/alert'
var webhookUrl = '${agentBaseUrl}${webhookEndpoint}'

// Action Group with Webhook
resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-${alertRuleName}'
  location: 'global'
  properties: {
    groupShortName: 'AIAgent'
    enabled: true
    webhookReceivers: [
      {
        name: 'ai-diagnostics-agent'
        serviceUri: webhookUrl
        useCommonAlertSchema: true
      }
    ]
  }
}

// Scheduled Query Alert Rule
resource alertRule 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: alertRuleName
  location: location
  properties: {
    displayName: 'Exception Detected - AI Agent Trigger'
    description: 'Triggers when new exceptions are detected in Application Insights. Calls the AI Diagnostics Agent to analyze and propose fixes.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    scopes: [
      empty(logAnalyticsId) ? appInsightsId : logAnalyticsId
    ]
    windowSize: 'PT5M'
    criteria: {
      allOf: [
        {
          query: 'AppExceptions | where TimeGenerated > ago(5m) | summarize Count = count() by ExceptionType, ProblemId | where Count >= 1'
          timeAggregation: 'Count'
          dimensions: []
          operator: 'GreaterThanOrEqual'
          threshold: exceptionThreshold
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: false  // Set to false so webhook fires on every evaluation when condition is met
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}

output alertRuleId string = alertRule.id
output actionGroupId string = actionGroup.id
output webhookUrl string = webhookUrl
output analysisMode string = analysisMode
