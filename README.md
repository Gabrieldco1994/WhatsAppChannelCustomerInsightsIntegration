# WhatsApp Multi-Provider Integration for Dynamics 365 Customer Insights

Custom channel for D365 Customer Insights - Journeys that enables WhatsApp messaging via **Twilio**, **Infobip**, or **Azure Communication Services (ACS)**.

## Features

- 🔄 **Multi-provider** — Switch between Twilio, Infobip, and ACS without code changes
- 📝 **Templates** — Send pre-approved WhatsApp template messages with dynamic placeholders
- ✅ **Delivery Reports** — Auto-detect provider format (Twilio form-encoded, Infobip JSON)
- 🚀 **One-click deploy** — Automated setup script for Azure + D365
- 🔒 **No hardcoded secrets** — All credentials stored in D365 entity and Azure Key Vault

## Architecture

```
D365 Journey → Custom API → Plugin → Provider API → WhatsApp
                                          ↓
                               Twilio / Infobip / ACS
                                          ↓
                          Delivery Callback → Azure Function → D365 Notification
```

## Prerequisites

- **Azure AD Global Admin** (or Application Administrator) — required to create App Registrations and grant admin consent
- **Power Platform System Administrator** — required to import solutions, create App Users, and configure security roles
- **Azure subscription** with permissions to create resources (Contributor or Owner)
- **Azure CLI** (`az`) installed
- **.NET 8 SDK**
- **Power Platform CLI** (`pac`) installed
- **bash**, **zip**, **curl**
- One of: Twilio account, Infobip account, or ACS resource with WhatsApp configured

## Quick Start

### 1. Clone the repository

```bash
git clone https://github.com/Gabrieldco1994/WhatsAppChannelCustomerInsightsIntegration.git
cd WhatsAppChannelCustomerInsightsIntegration
```

### 2. Authenticate with Azure and Power Platform

**Azure CLI** — login to the tenant that contains your D365 environment:

```bash
az login --tenant <YOUR_TENANT_ID>
az account set --subscription <YOUR_SUBSCRIPTION_ID>
```

**Power Platform CLI** — authenticate to the target D365 organization:

```bash
pac auth create --url https://<YOUR_ORG>.crm.dynamics.com
```

> 💡 You can find your Tenant ID in **Azure Portal → Azure Active Directory → Overview**.
> Your Org URL is visible in **Power Platform Admin Center → Environments → your env → Environment URL**.

### 3. Run the setup script

```bash
./setup.sh
```

The script will prompt for:
- D365 Organization URL
- Azure Tenant ID & Subscription ID
- Azure Region
- Resource naming

It automatically creates:
- Azure App Registration + client secret
- Storage Account + Function App (Flex Consumption)
- Managed Identity + RBAC
- Builds and deploys the Azure Function
- Outputs the Webhook URL

### 3. Manual steps after setup

The script will display these remaining steps:

#### a) Grant admin consent
Azure Portal → Azure AD → App Registrations → your app → API Permissions → Grant admin consent

#### b) Create Application User in D365
Power Platform Admin Center → Environments → your env → Settings → Users + Permissions → Application Users → + New app user → Select the App Registration → Assign role: **Cxp Channel Definitions Services User**

#### c) Import D365 Solution
If the script didn't import automatically:
```bash
pac solution import --path output/WhatsAppChannel.zip --activate-plugins
```

#### d) Build and Register the Plugin
```bash
cd src/Plugins
dotnet build -c Release
```

Get the plugin assembly ID:
```bash
pac org fetch --xml "<fetch><entity name='pluginassembly'><attribute name='pluginassemblyid'/><attribute name='name'/><filter><condition attribute='name' operator='like' value='%WhatsApp%'/></filter></entity></fetch>"
```

Push the plugin DLL (replace `<GUID>` with the ID from above):
```bash
pac plugin push --pluginId <GUID> --pluginFile bin/Release/net462/WhatsAppACSChannel.Plugins.dll --type Assembly
```

Then register a Plugin Step is **not needed** — the solution already includes the Custom API binding to the plugin.

#### e) Configure Channel Instance
In D365: Settings → Customer Engagement → Channel Definitions → WhatsApp → Create Instance

Fill the fields:

| Field | Description |
|-------|-------------|
| **Provider** | Select: Twilio, Infobip, or ACS |
| **API Key** | See table below |
| **Secret** | See table below |
| **Webhook URL** | Paste the URL from setup script output |

## Provider Configuration

### Twilio

| Field | Value |
|-------|-------|
| Provider | Twilio |
| API Key | Account SID (starts with `AC...`) |
| Secret | Auth Token |
| Sender Number | WhatsApp-enabled Twilio number (e.g. `+14155238886`) |

**Also configure in Twilio Console:**
- Messaging → WhatsApp Sandbox → Status Callback URL → paste Webhook URL

### Infobip

| Field | Value |
|-------|-------|
| Provider | Infobip |
| API Key | API Key from Infobip Portal |
| Secret | Base URL (e.g. `xxxxx.api.infobip.com`) |
| Sender Number | Infobip WhatsApp sender number |

### Azure Communication Services

| Field | Value |
|-------|-------|
| Provider | ACS |
| API Key | ACS Endpoint URL (e.g. `https://xxx.communication.azure.com`) |
| Secret | ACS Access Key |
| Sender Number | Channel Registration ID (GUID) |

## Templates

To send template messages, configure in the Journey message editor:

| Message Part | Description |
|---|---|
| **Text** | Placeholder values, comma-separated (e.g. `{{FirstName}},{{City}}`) |
| **Template ID / Name** | Twilio: Content SID (`HXxxx`) / Infobip: template name / ACS: template name |
| **Template Language** | Language code (e.g. `en`, `pt_BR`). Defaults to `en` |

D365 resolves `{{FirstName}}`, `{{City}}` etc. to actual contact/lead values at send time.

## Project Structure

```
WhatsAppACSChannel/
├── setup.sh                    # Automated deployment script
├── infra/
│   └── main.bicep              # Azure infrastructure template
├── src/
│   ├── Plugins/                # D365 Plugin (outbound message handling)
│   │   ├── OutboundWhatsAppPlugin.cs
│   │   ├── Models/
│   │   └── WhatsAppACSChannel.Plugins.csproj
│   ├── AzureFunction/          # Delivery report webhook
│   │   ├── TwilioDeliveryReport.cs
│   │   ├── Program.cs
│   │   └── TwilioWebhook.csproj
│   └── Solution/               # D365 Solution XML (reference)
└── output/                     # Packaged solution (.zip)
```

## Troubleshooting

### Plugin errors
Check plugin trace logs in D365:
- Settings → Customizations → Plugin Trace Log

### Function not receiving callbacks
1. Verify the Webhook URL is correct in the channel instance
2. Check Function App logs: Azure Portal → Function App → Monitor
3. For Twilio: verify Status Callback URL in Twilio Console
4. For Infobip: delivery reports use per-message `notifyUrl`

### Messages not delivered
- **Twilio/Infobip**: Ensure 24h session is active (send a message TO the number first) or use templates
- **ACS**: Ensure WhatsApp Business number is verified and active

### Authentication errors (invalid_client)
- Verify App Registration Client Secret is the **Value**, not the **Secret ID**
- Check if the secret hasn't expired
- Ensure admin consent was granted

## License

MIT
