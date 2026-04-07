#!/bin/bash
# ============================================================
# WhatsApp Multi-Provider Channel — Automated Setup
# ============================================================
# This script deploys the complete solution:
#   1. Azure App Registration (for Function → D365 auth)
#   2. Azure Infrastructure (Storage, Function App, RBAC)
#   3. Azure Function code (build + zip deploy)
#   4. D365 Solution (import + publish)
#   5. D365 App User + Security Role
#
# Prerequisites:
#   - Azure CLI (az) installed and logged in
#   - .NET 8 SDK
#   - Power Platform CLI (pac) installed and authenticated
#   - bash, zip, curl
#
# Usage: ./setup.sh
# ============================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "============================================"
echo " WhatsApp Multi-Provider Channel Setup"
echo "============================================"
echo ""

# ---- Collect Parameters ----
read -p "D365 Organization URL (e.g. https://orgXXXXX.crm.dynamics.com): " D365_URL
read -p "Azure Tenant ID: " TENANT_ID
read -p "Azure Subscription ID: " SUBSCRIPTION_ID
read -p "Azure Region (e.g. brazilsouth, eastus): " LOCATION
read -p "Base name for resources (lowercase, e.g. whatsapp-channel): " BASE_NAME
read -p "Resource Group name (will be created if not exists): " RESOURCE_GROUP

echo ""
echo "--- Configuration ---"
echo "D365 URL:       $D365_URL"
echo "Tenant:         $TENANT_ID"
echo "Subscription:   $SUBSCRIPTION_ID"
echo "Region:         $LOCATION"
echo "Base Name:      $BASE_NAME"
echo "Resource Group: $RESOURCE_GROUP"
echo ""
read -p "Continue? (y/n): " CONFIRM
if [ "$CONFIRM" != "y" ]; then echo "Aborted."; exit 1; fi

# ---- Set subscription ----
echo ""
echo "[1/7] Setting Azure subscription..."
az account set --subscription "$SUBSCRIPTION_ID"

# ---- Create Resource Group ----
echo "[2/7] Creating Resource Group..."
az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output none 2>/dev/null || true

# ---- Create App Registration ----
echo "[3/7] Creating App Registration..."
APP_NAME="app-${BASE_NAME}-d365"
APP_ID=$(az ad app create --display-name "$APP_NAME" --query appId --output tsv 2>/dev/null)
echo "  App ID: $APP_ID"

# Create client secret (valid 2 years)
CLIENT_SECRET=$(az ad app credential reset --id "$APP_ID" --years 2 --query password --output tsv 2>/dev/null)
echo "  Client Secret created."

# Create service principal
az ad sp create --id "$APP_ID" --output none 2>/dev/null || true

# Add Dynamics CRM API permission (user_impersonation)
az ad app permission add --id "$APP_ID" \
  --api 00000007-0000-0000-c000-000000000000 \
  --api-permissions 78ce3f0f-a1ce-49c2-8cde-64b5c0896db4=Scope \
  --output none 2>/dev/null || true

echo "  NOTE: You may need to grant admin consent in Azure Portal"
echo "        Azure AD → App Registrations → $APP_NAME → API Permissions → Grant admin consent"

# ---- Deploy Infrastructure (Bicep) ----
echo ""
echo "[4/7] Deploying Azure Infrastructure..."
echo "  (This may take 2-3 minutes...)"
DEPLOY_OUTPUT=$(az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$SCRIPT_DIR/infra/main.bicep" \
  --parameters \
    baseName="$BASE_NAME" \
    location="$LOCATION" \
    d365Url="$D365_URL" \
    tenantId="$TENANT_ID" \
    d365ClientId="$APP_ID" \
    d365ClientSecret="$CLIENT_SECRET" \
  --query "properties.outputs" \
  --output json 2>&1)

if echo "$DEPLOY_OUTPUT" | python3 -c "import sys,json; json.load(sys.stdin)" 2>/dev/null; then
  FUNCTION_APP_NAME=$(echo "$DEPLOY_OUTPUT" | python3 -c "import sys,json; print(json.load(sys.stdin)['functionAppName']['value'])")
  FUNCTION_HOSTNAME=$(echo "$DEPLOY_OUTPUT" | python3 -c "import sys,json; print(json.load(sys.stdin)['functionAppHostname']['value'])")
else
  echo "  ERROR: Bicep deployment failed. Output:"
  echo "$DEPLOY_OUTPUT"
  exit 1
fi

echo "  Function App: $FUNCTION_APP_NAME"
echo "  Hostname:     $FUNCTION_HOSTNAME"

# Wait for RBAC propagation
echo "  Waiting 30s for RBAC propagation..."
sleep 30

# ---- Build & Deploy Function ----
echo ""
echo "[5/7] Building and deploying Azure Function..."
cd "$SCRIPT_DIR/src/AzureFunction"
dotnet publish -c Release -o ./publish --nologo -v quiet 2>/dev/null
cd publish
rm -f ../function.zip
zip -r ../function.zip . -q
cd ..

az functionapp deployment source config-zip \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME" \
  --src function.zip \
  --output none 2>/dev/null

# Get function key
echo "  Waiting for function to start..."
sleep 15
FUNC_KEY=$(az functionapp function keys list \
  --name "$FUNCTION_APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --function-name DeliveryReport \
  --query "default" --output tsv 2>/dev/null)

WEBHOOK_URL="https://${FUNCTION_HOSTNAME}/api/DeliveryReport?code=${FUNC_KEY}"
echo "  Webhook URL: $WEBHOOK_URL"

# Clean up publish artifacts
rm -rf publish function.zip
cd "$SCRIPT_DIR"

# ---- Import D365 Solution ----
echo ""
echo "[6/7] Importing D365 Solution..."
if [ -f "$SCRIPT_DIR/output/WhatsAppChannel.zip" ]; then
  pac solution import --path "$SCRIPT_DIR/output/WhatsAppChannel.zip" --activate-plugins 2>/dev/null || {
    echo "  WARNING: Solution import failed. You may need to import manually."
    echo "  File: $SCRIPT_DIR/output/WhatsAppChannel.zip"
  }
else
  echo "  WARNING: Solution file not found at output/WhatsAppChannel.zip"
  echo "  You need to export the solution from a source environment first."
  echo "  Run: pac solution export --name WhatsAppChannel --path output/WhatsAppChannel.zip"
fi

# ---- Configure Security Role ----
echo ""
echo "[7/7] Configuring security role..."
D365_TOKEN=$(az account get-access-token --resource "$D365_URL" --query accessToken --output tsv 2>/dev/null)

if [ -n "$D365_TOKEN" ]; then
  # Get Read privilege ID for new_whatsappchannelinstance
  PRIV_ID=$(curl -s "${D365_URL}/api/data/v9.2/privileges" \
    -H "Authorization: Bearer $D365_TOKEN" \
    -H "OData-MaxVersion: 4.0" \
    -G --data-urlencode "\$filter=name eq 'prvReadnew_whatsappchannelinstance'" \
    --data-urlencode "\$select=privilegeid" | python3 -c "
import sys, json
d = json.load(sys.stdin)
print(d['value'][0]['privilegeid'] if d.get('value') else '')
" 2>/dev/null)

  if [ -n "$PRIV_ID" ]; then
    # Find "Marketing Services User Extensible Role"
    ROLE_ID=$(curl -s "${D365_URL}/api/data/v9.2/roles" \
      -H "Authorization: Bearer $D365_TOKEN" \
      -H "OData-MaxVersion: 4.0" \
      -G --data-urlencode "\$filter=name eq 'Marketing Services User Extensible Role'" \
      --data-urlencode "\$select=roleid" | python3 -c "
import sys, json
d = json.load(sys.stdin)
print(d['value'][0]['roleid'] if d.get('value') else '')
" 2>/dev/null)

    if [ -n "$ROLE_ID" ]; then
      curl -s -w "" -X POST \
        "${D365_URL}/api/data/v9.2/roles(${ROLE_ID})/Microsoft.Dynamics.CRM.AddPrivilegesRole" \
        -H "Authorization: Bearer $D365_TOKEN" \
        -H "Content-Type: application/json" \
        -H "OData-MaxVersion: 4.0" \
        -d "{\"Privileges\": [{\"PrivilegeId\": \"$PRIV_ID\", \"Depth\": \"3\"}]}" >/dev/null 2>&1
      echo "  Read privilege added to 'Marketing Services User Extensible Role' (Organization level)"
    else
      echo "  WARNING: 'Marketing Services User Extensible Role' not found. Add Read privilege manually."
    fi
  else
    echo "  WARNING: Entity not found yet. Add Read privilege after solution import."
  fi
else
  echo "  WARNING: Could not get D365 token. Configure security role manually."
fi

# ---- Summary ----
echo ""
echo "============================================"
echo " ✅ Setup Complete!"
echo "============================================"
echo ""
echo " Azure Resources:"
echo "   Function App:  $FUNCTION_APP_NAME"
echo "   Hostname:      $FUNCTION_HOSTNAME"
echo ""
echo " App Registration:"
echo "   Client ID:     $APP_ID"
echo "   Client Secret: (saved in Function App settings)"
echo ""
echo " Webhook URL (paste in channel instance):"
echo "   $WEBHOOK_URL"
echo ""
echo " ── Remaining Manual Steps ──"
echo ""
echo " 1. Grant admin consent for App Registration:"
echo "    Azure Portal → Azure AD → App Registrations → $APP_NAME → API Permissions"
echo ""
echo " 2. Create Application User in D365:"
echo "    Power Platform Admin Center → Environments → your env → Settings"
echo "    → Users + Permissions → Application Users → + New app user"
echo "    → App: $APP_ID"
echo "    → Security Role: 'Cxp Channel Definitions Services User'"
echo ""
echo " 3. Configure Channel Instance in D365:"
echo "    Settings → Customer Engagement → Channel Definitions"
echo "    → Select 'WhatsApp' → Create Channel Instance"
echo "    → Fill: Provider, API Key, Secret, Webhook URL"
echo ""
echo " Provider Configuration:"
echo "   ┌──────────┬─────────────────────────┬─────────────────────────┐"
echo "   │ Provider │ API Key                 │ Secret                  │"
echo "   ├──────────┼─────────────────────────┼─────────────────────────┤"
echo "   │ Twilio   │ Account SID             │ Auth Token              │"
echo "   │ Infobip  │ API Key                 │ Base URL (xxx.api...)   │"
echo "   │ ACS      │ Endpoint URL            │ Access Key              │"
echo "   └──────────┴─────────────────────────┴─────────────────────────┘"
echo ""
echo " Webhook URL (paste in channel instance):"
echo "   $WEBHOOK_URL"
echo ""
echo " For Twilio: also set Status Callback URL in Twilio Console"
echo "   $WEBHOOK_URL"
echo ""
echo "============================================"
