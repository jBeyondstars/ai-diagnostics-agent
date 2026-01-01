# Azure Monitor Alert Setup

This guide explains how to configure Azure Monitor to automatically trigger the AI Diagnostics Agent when exceptions occur in Application Insights.

## Architecture

```
Exception occurs
      ↓
Application Insights logs it
      ↓
Azure Monitor Alert Rule detects it (every 5 min)
      ↓
Action Group triggers webhook
      ↓
AI Agent receives alert
      ↓
Agent analyzes exception + creates PR
```

## Prerequisites

- Azure subscription with Application Insights
- AI Diagnostics Agent deployed and accessible via HTTPS
- Azure CLI installed (for script method)

## Option 1: Deploy with Bicep (Recommended)

### 1. Update Parameters

Edit `infra/alert-rule.parameters.json`:

```json
{
  "parameters": {
    "appInsightsId": {
      "value": "/subscriptions/YOUR_SUB/resourceGroups/YOUR_RG/providers/microsoft.insights/components/YOUR_APP_INSIGHTS"
    },
    "workspaceId": {
      "value": "YOUR_WORKSPACE_ID"
    },
    "webhookUrl": {
      "value": "https://your-agent.azurecontainerapps.io/api/diagnostics/webhook/alert"
    }
  }
}
```

### 2. Deploy

```bash
az deployment group create \
  --resource-group your-resource-group \
  --template-file infra/alert-rule.bicep \
  --parameters @infra/alert-rule.parameters.json
```

## Option 2: Azure CLI Script

```bash
# Edit the variables in the script first
chmod +x infra/setup-alert.sh
./infra/setup-alert.sh
```

## Option 3: Azure Portal (Manual)

### Step 1: Create Action Group

1. Go to **Azure Monitor** > **Alerts** > **Action groups**
2. Click **Create**
3. Fill in:
   - **Name**: `ag-ai-diagnostics-agent`
   - **Short name**: `AIAgent`
4. In **Actions** tab:
   - **Action type**: Webhook
   - **Name**: `ai-agent-webhook`
   - **URI**: `https://your-agent-url/api/diagnostics/webhook/alert`
   - **Enable common alert schema**: Yes
5. Click **Create**

### Step 2: Create Alert Rule

1. Go to your **Application Insights** resource
2. Click **Alerts** > **Create** > **Alert rule**
3. **Condition**:
   - Signal: **Custom log search**
   - Query:
     ```kusto
     exceptions
     | where timestamp > ago(5m)
     | summarize Count = count() by type
     | where Count >= 1
     ```
   - **Threshold**: Greater than 0
   - **Evaluation frequency**: 5 minutes
   - **Lookback period**: 5 minutes
4. **Actions**:
   - Select the action group created above
5. **Details**:
   - **Alert rule name**: `ai-agent-exception-alert`
   - **Severity**: Warning (Sev 2)
6. Click **Create**

## Webhook Endpoints

The agent exposes two webhook endpoints:

### `/api/diagnostics/webhook/alert`

Receives Azure Monitor common alert schema payloads.

```bash
# Test with simulated alert
curl -X POST https://your-agent/api/diagnostics/webhook/alert \
  -H "Content-Type: application/json" \
  -d '{
    "schemaId": "azureMonitorCommonAlertSchema",
    "data": {
      "essentials": {
        "alertId": "test-123",
        "alertRule": "test-rule",
        "severity": "Sev2"
      }
    }
  }'
```

### `/api/diagnostics/webhook/trigger`

Simple trigger endpoint for testing (no payload required).

```bash
# Trigger analysis directly
curl -X POST "https://your-agent/api/diagnostics/webhook/trigger?createPR=true"
```

## Debouncing

The webhook endpoint includes automatic debouncing:

- **Default**: 5 minutes between analyses for the same alert rule
- Prevents alert storms from creating multiple PRs
- Uses Redis cache (or in-memory for dev)

## Troubleshooting

### Alert not firing

1. Check if exceptions are being logged:
   ```kusto
   exceptions
   | where timestamp > ago(1h)
   | summarize count() by type
   ```

2. Verify alert rule is enabled in Azure Monitor

### Webhook not called

1. Check Action Group configuration
2. Verify the webhook URL is publicly accessible
3. Check Azure Monitor > Alerts > Alert history

### Agent not creating PR

1. Check agent logs for errors
2. Verify GitHub token permissions
3. Check deduplication cache (might be skipping due to recent analysis)

## Cost Considerations

- **Alert Rule**: ~$0.10/month per rule
- **Action Group**: Free (webhook actions)
- **Agent execution**: Depends on LLM costs (~$0.05 per analysis)

For high-volume applications, consider:
- Increasing evaluation frequency (e.g., 15 min instead of 5)
- Raising exception threshold (e.g., 5+ occurrences)
- Using sampling in Application Insights
