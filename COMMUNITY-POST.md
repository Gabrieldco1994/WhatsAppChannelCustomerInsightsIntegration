# WhatsApp Multi-Provider Channel for Dynamics 365 Customer Insights – Journeys

As Solution Engineers, one of the most common questions we get from customers is:
**"How do I send WhatsApp messages from my Journeys?"**

The answer usually involves navigating between different providers, configuring Azure resources, registering plugins, and stitching it all together manually. That's a lot of moving parts — and a lot of room for things to break during a demo or a POC.

Over the past few weeks, I built a **complete, production-ready WhatsApp custom channel** that works with **Twilio, Infobip, or Azure Communication Services** — all from a single configuration. No hardcoded values, no recompilation needed, and fully automated deployment.

---

## What it does

A multi-provider WhatsApp channel for D365 Customer Insights – Journeys that lets marketers send WhatsApp messages (text and templates) directly from Journeys, with delivery report tracking.

**One channel, three providers:**

| Capability | Twilio | Infobip | ACS |
|---|---|---|---|
| Text messages | ✅ | ✅ | ✅ |
| Template messages with dynamic placeholders | ✅ | ✅ | ✅ |
| Delivery reports | ✅ | ✅ | 🔜 |

The provider is selected in the channel configuration — no code changes needed to switch.

---

## How it works

```
D365 Journey → Custom API → Plugin (auto-routes by provider) → WhatsApp
                                                                    ↓
                                              Delivery Callback → Azure Function → D365 Notification
```

- **Plugin**: Reads the provider setting from the channel instance and routes to the correct API (Twilio REST, Infobip API, or ACS Advanced Messaging with HMAC auth)
- **Azure Function**: Single endpoint that auto-detects the provider format (Twilio form-encoded vs Infobip JSON) and forwards delivery reports to D365
- **Templates**: Uses D365's native `{{FirstName}}`, `{{City}}` variables — resolved at send time and mapped to provider-specific placeholder formats

---

## What makes it different

- **Provider-agnostic**: Switch between Twilio, Infobip, and ACS without touching code
- **Zero hardcoded secrets**: All credentials stored in D365 entity fields, webhook URL is dynamic
- **Automated deployment**: A single `setup.sh` script creates everything — Azure infrastructure (Bicep), Function App, App Registration, RBAC, solution import, and security roles
- **Ready to clone and deploy**: Everything is on GitHub, fully functional

---

## Quick Start

```bash
git clone https://github.com/Gabrieldco1994/WhatsAppChannelCustomerInsightsIntegration.git
cd WhatsAppChannelCustomerInsightsIntegration
./setup.sh
```

The script handles:
1. Azure App Registration + client secret
2. Infrastructure deployment (Storage, Function App, Managed Identity, RBAC)
3. Azure Function build and deployment
4. D365 solution import
5. Security role configuration

After setup, configure your channel instance with your provider credentials and you're sending WhatsApp messages from Journeys.

---

## A few notes

- You can share this with customers. Everything is on my [GitHub](https://github.com/Gabrieldco1994/WhatsAppChannelCustomerInsightsIntegration)
- These are not official Microsoft assets
- Everything is fully functional — nothing hardcoded, nothing mocked
- Prerequisites: Azure CLI, .NET 8 SDK, Power Platform CLI

---

## The SE perspective

For demos, this solves a real pain point. Instead of explaining *"you could build a custom channel that..."*, you show it working end-to-end in minutes. The customer sees:

1. A Journey with a WhatsApp step
2. Dynamic content with contact variables
3. A real message arriving on their phone
4. Delivery confirmation back in D365

No slides. No "imagine if". Just the platform doing what it should.

This isn't about building custom assets for the sake of customization. It's about showing customers that **D365 Customer Insights can reach their audience on the channels that matter** — with the provider they already use.

---

If you're working on similar channel integrations or have ideas for extending this (inbound messages, media support, more providers), I'd love to collaborate. Let's make our demos tell better stories.
