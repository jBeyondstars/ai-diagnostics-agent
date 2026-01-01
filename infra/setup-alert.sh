#!/bin/bash
# Setup Azure Monitor Alert Rule for AI Diagnostics Agent
# This script creates an Action Group and Alert Rule that triggers the agent on exceptions

set -e

# Configuration - Update these values
RESOURCE_GROUP="your-resource-group"
LOCATION="westeurope"
APP_INSIGHTS_NAME="your-app-insights"
WEBHOOK_URL="https://your-agent-url/api/diagnostics/webhook/alert"
ALERT_NAME="ai-agent-exception-alert"
ACTION_GROUP_NAME="ag-ai-agent"

echo "Creating Action Group..."
az monitor action-group create \
  --name "$ACTION_GROUP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --short-name "AIAgent" \
  --action webhook ai-agent-webhook "$WEBHOOK_URL" \
  --output table

echo "Getting Application Insights resource ID..."
APP_INSIGHTS_ID=$(az monitor app-insights component show \
  --app "$APP_INSIGHTS_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query id -o tsv)

echo "App Insights ID: $APP_INSIGHTS_ID"

echo "Creating Alert Rule..."
az monitor scheduled-query create \
  --name "$ALERT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --scopes "$APP_INSIGHTS_ID" \
  --condition "count 'exceptions | where timestamp > ago(5m) | summarize Count = count() by type' > 0" \
  --condition-query "exceptions | where timestamp > ago(5m) | summarize Count = count() by type | where Count >= 1" \
  --description "Triggers AI Diagnostics Agent when exceptions are detected" \
  --evaluation-frequency 5m \
  --window-size 5m \
  --severity 2 \
  --action-groups "/subscriptions/$(az account show --query id -o tsv)/resourceGroups/$RESOURCE_GROUP/providers/microsoft.insights/actionGroups/$ACTION_GROUP_NAME" \
  --output table

echo ""
echo "Setup complete!"
echo ""
echo "Alert Rule: $ALERT_NAME"
echo "Action Group: $ACTION_GROUP_NAME"
echo "Webhook URL: $WEBHOOK_URL"
echo ""
echo "The agent will now be triggered automatically when exceptions occur."
