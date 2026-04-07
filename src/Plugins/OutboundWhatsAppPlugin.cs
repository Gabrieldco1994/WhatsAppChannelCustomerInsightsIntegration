using System;
using System.IO;
using System.Net;
using System.Text;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Globalization;
using Microsoft.Xrm.Sdk;

namespace WhatsAppACSChannel.Plugins
{
    /// <summary>
    /// Multi-provider WhatsApp outbound plugin.
    /// Supports Twilio, Infobip, and ACS based on the new_provider field.
    /// 
    /// Channel Instance fields:
    ///   new_provider -> OptionSet: 1=Twilio, 2=Infobip, 3=ACS
    ///   new_acsconnectionstring -> credentials (format varies by provider)
    ///     Twilio:  "AccountSID:AuthToken"
    ///     Infobip: "ApiKey:BaseURL"
    ///     ACS:     ACS connection string
    ///   new_whatsappchannelregistrationid -> sender number
    /// </summary>
    public class OutboundWhatsAppPlugin : IPlugin
    {
        private const int ProviderTwilio = 1;
        private const int ProviderInfobip = 2;
        private const int ProviderACS = 3;

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var orgServiceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var orgService = orgServiceFactory.CreateOrganizationService(context.UserId);

            string requestId = null;
            Guid channelDefId = Guid.Empty;

            try
            {
                var payloadJson = (string)context.InputParameters["payload"];
                tracingService.Trace("Received payload: {0}", payloadJson);

                var payload = JsonHelper.Deserialize<Models.OutboundPayload>(payloadJson);
                requestId = payload.RequestId;
                channelDefId = payload.ChannelDefinitionId;

                var config = RetrieveChannelConfig(orgService, tracingService, payload.From);
                var apiKey = config.GetAttributeValue<string>("new_apikey");
                var secret = config.GetAttributeValue<string>("new_secret");
                var webhookUrl = config.GetAttributeValue<string>("new_webhookurl") ?? "";
                var providerValue = config.GetAttributeValue<OptionSetValue>("new_provider");
                int provider = providerValue != null ? providerValue.Value : ProviderTwilio;
                var fromNumber = payload.From;

                tracingService.Trace("Provider: {0}, From: {1}, Webhook: {2}", provider, fromNumber, webhookUrl);

                if (string.IsNullOrEmpty(apiKey))
                    throw new InvalidPluginExecutionException("API Key not configured in channel instance.");
                if (string.IsNullOrEmpty(secret))
                    throw new InvalidPluginExecutionException("Secret not configured in channel instance.");

                string messageText = ExtractMessagePart(payload, payloadJson, "text");
                string templateName = ExtractMessagePart(payload, payloadJson, "templateName");
                string templateLanguage = ExtractMessagePart(payload, payloadJson, "templateLanguage") ?? "en";

                tracingService.Trace("Text: {0}, Template: {1}, Lang: {2}", messageText ?? "null", templateName ?? "null", templateLanguage);

                string responseBody;
                string messageId;
                string status;

                switch (provider)
                {
                    case ProviderTwilio:
                        responseBody = SendViaTwilio(apiKey, secret, fromNumber, payload.To, messageText, templateName, webhookUrl, tracingService);
                        messageId = ExtractField(responseBody, "\"sid\"");
                        status = ExtractField(responseBody, "\"status\"");
                        break;

                    case ProviderInfobip:
                        responseBody = SendViaInfobip(apiKey, secret, fromNumber, payload.To, messageText, templateName, templateLanguage, webhookUrl, tracingService);
                        messageId = ExtractField(responseBody, "\"messageId\"");
                        var groupName = ExtractField(responseBody, "\"groupName\"");
                        status = (groupName == "REJECTED") ? "failed" : "queued";
                        break;

                    case ProviderACS:
                        responseBody = SendViaACS(apiKey, secret, fromNumber, payload.To, messageText, templateName, tracingService);
                        messageId = ExtractField(responseBody, "\"messageId\"");
                        status = "queued";
                        break;

                    default:
                        throw new InvalidPluginExecutionException("Unknown provider: " + provider);
                }

                tracingService.Trace("Provider response: {0}", responseBody);

                var response = new Models.OutboundResponse
                {
                    ChannelDefinitionId = channelDefId,
                    MessageId = messageId ?? "",
                    RequestId = requestId,
                    Status = (status == "failed" || status == "undelivered") ? "NotSent" : "Sent"
                };
                context.OutputParameters["response"] = JsonHelper.Serialize(response);
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (WebException wex)
            {
                string errorDetail = "";
                if (wex.Response != null)
                {
                    using (var sr = new StreamReader(wex.Response.GetResponseStream()))
                        errorDetail = sr.ReadToEnd();
                }
                tracingService.Trace("API error: {0} - {1}", wex.Message, errorDetail);

                var response = new Models.OutboundResponse
                {
                    ChannelDefinitionId = channelDefId,
                    RequestId = requestId ?? "",
                    Status = "NotSent"
                };
                context.OutputParameters["response"] = JsonHelper.Serialize(response);
            }
            catch (Exception ex)
            {
                tracingService.Trace("Error: {0}", ex.ToString());

                var response = new Models.OutboundResponse
                {
                    ChannelDefinitionId = channelDefId,
                    RequestId = requestId ?? "",
                    Status = "NotSent"
                };
                context.OutputParameters["response"] = JsonHelper.Serialize(response);
            }
        }

        #region Twilio Provider

        private string SendViaTwilio(string accountSid, string authToken, string fromNumber, string toNumber, string messageText, string templateName, string webhookUrl, ITracingService trace)
        {
            var url = string.Format("https://api.twilio.com/2010-04-01/Accounts/{0}/Messages.json", accountSid);

            string postData;
            if (!string.IsNullOrEmpty(templateName))
            {
                trace.Trace("Sending TEMPLATE via Twilio to {0}", toNumber);
                postData = string.Format("From={0}&To={1}&ContentSid={2}&StatusCallback={3}",
                    Uri.EscapeDataString("whatsapp:" + fromNumber),
                    Uri.EscapeDataString("whatsapp:" + toNumber),
                    Uri.EscapeDataString(templateName),
                    Uri.EscapeDataString(webhookUrl));
                if (!string.IsNullOrEmpty(messageText))
                    postData += "&Body=" + Uri.EscapeDataString(messageText);
            }
            else
            {
                if (string.IsNullOrEmpty(messageText))
                    throw new InvalidPluginExecutionException("Message text is empty and no template specified.");
                trace.Trace("Sending TEXT via Twilio to {0}", toNumber);
                postData = string.Format("From={0}&To={1}&Body={2}&StatusCallback={3}",
                    Uri.EscapeDataString("whatsapp:" + fromNumber),
                    Uri.EscapeDataString("whatsapp:" + toNumber),
                    Uri.EscapeDataString(messageText),
                    Uri.EscapeDataString(webhookUrl));
            }

            return HttpPost(url, "application/x-www-form-urlencoded", postData,
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(accountSid + ":" + authToken)), trace);
        }

        #endregion

        #region Infobip Provider

        private string SendViaInfobip(string apiKey, string baseUrl, string fromNumber, string toNumber, string messageText, string templateName, string templateLanguage, string webhookUrl, ITracingService trace)
        {

            if (!string.IsNullOrEmpty(templateName))
            {
                trace.Trace("Sending TEMPLATE via Infobip to {0}", toNumber);
                var url = string.Format("https://{0}/whatsapp/1/message/template", baseUrl);

                // Build placeholders array from comma-separated text
                var placeholdersJson = "[]";
                if (!string.IsNullOrEmpty(messageText))
                {
                    var parts = messageText.Split(',');
                    var sb = new StringBuilder("[");
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (i > 0) sb.Append(",");
                        sb.Append("\"");
                        sb.Append(EscapeJsonString(parts[i].Trim()));
                        sb.Append("\"");
                    }
                    sb.Append("]");
                    placeholdersJson = sb.ToString();
                }
                trace.Trace("Placeholders: {0}", placeholdersJson);

                var body = string.Format(
                    "{{\"messages\":[{{\"from\":\"{0}\",\"to\":\"{1}\",\"content\":{{\"templateName\":\"{2}\",\"templateData\":{{\"body\":{{\"placeholders\":{3}}}}},\"language\":\"{4}\"}},\"notifyUrl\":\"{5}\"}}]}}",
                    fromNumber, toNumber, EscapeJsonString(templateName),
                    placeholdersJson,
                    EscapeJsonString(templateLanguage), EscapeJsonString(webhookUrl));
                return HttpPost(url, "application/json", body, "App " + apiKey, trace);
            }
            else
            {
                if (string.IsNullOrEmpty(messageText))
                    throw new InvalidPluginExecutionException("Message text is empty and no template specified.");
                trace.Trace("Sending TEXT via Infobip to {0}", toNumber);
                var url = string.Format("https://{0}/whatsapp/1/message/text", baseUrl);
                var body = string.Format(
                    "{{\"from\":\"{0}\",\"to\":\"{1}\",\"content\":{{\"text\":\"{2}\"}},\"notifyUrl\":\"{3}\"}}",
                    fromNumber, toNumber, EscapeJsonString(messageText), EscapeJsonString(webhookUrl));
                return HttpPost(url, "application/json", body, "App " + apiKey, trace);
            }
        }

        #endregion

        #region ACS Provider

        private string SendViaACS(string acsEndpoint, string accessKey, string fromNumber, string toNumber, string messageText, string templateName, ITracingService trace)
        {
            // ACS API Key = endpoint (e.g. https://xxx.communication.azure.com)
            // ACS Secret = access key
            // fromNumber is the channel registration ID from D365 contact point
            // We need the channelRegistrationId - stored in a known location or derived
            var channelRegId = fromNumber; // In ACS, the "from" in D365 maps to channelRegistrationId

            string body;
            if (!string.IsNullOrEmpty(templateName))
            {
                trace.Trace("Sending TEMPLATE via ACS to {0}", toNumber);
                body = string.Format(
                    "{{\"channelRegistrationId\":\"{0}\",\"to\":[\"{1}\"],\"kind\":\"template\",\"template\":{{\"name\":\"{2}\",\"language\":\"{3}\"}}}}",
                    EscapeJsonString(channelRegId), toNumber, EscapeJsonString(templateName), "en");
            }
            else
            {
                if (string.IsNullOrEmpty(messageText))
                    throw new InvalidPluginExecutionException("Message text is empty and no template specified.");
                trace.Trace("Sending TEXT via ACS to {0}", toNumber);
                body = string.Format(
                    "{{\"channelRegistrationId\":\"{0}\",\"to\":[\"{1}\"],\"kind\":\"text\",\"content\":\"{2}\"}}",
                    EscapeJsonString(channelRegId), toNumber, EscapeJsonString(messageText));
            }

            var path = "/messages/notifications:send?api-version=2024-08-30";
            var url = acsEndpoint.TrimEnd('/') + path;
            var host = new Uri(acsEndpoint).Host;

            return AcsPost(url, path, host, accessKey, body, trace);
        }

        private string AcsPost(string url, string path, string host, string accessKey, string body, ITracingService trace)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            trace.Trace("Calling ACS: {0}", url);

            // HMAC-SHA256 authentication
            var date = DateTime.UtcNow.ToString("R");
            byte[] contentHash;
            using (var sha256 = SHA256.Create())
            {
                contentHash = sha256.ComputeHash(bodyBytes);
            }
            var contentHashBase64 = Convert.ToBase64String(contentHash);

            var stringToSign = string.Format("POST\n{0}\n{1};{2};{3}", path, date, host, contentHashBase64);
            var keyBytes = Convert.FromBase64String(accessKey);
            byte[] signatureBytes;
            using (var hmac = new HMACSHA256(keyBytes))
            {
                signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
            }
            var signature = Convert.ToBase64String(signatureBytes);
            var authorization = string.Format("HMAC-SHA256 SignedHeaders=date;host;x-ms-content-sha256&Signature={0}", signature);

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.ContentLength = bodyBytes.Length;
            request.Headers.Add("Authorization", authorization);
            request.Headers.Add("x-ms-content-sha256", contentHashBase64);
            request.Date = DateTime.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

            using (var stream = request.GetRequestStream())
            {
                stream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                return reader.ReadToEnd();
            }
        }

        #endregion

        #region HTTP Helper

        private string HttpPost(string url, string contentType, string body, string authorization, ITracingService trace)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            trace.Trace("Calling: {0}", url);

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = contentType;
            request.Headers.Add("Authorization", authorization);
            request.ContentLength = bodyBytes.Length;

            using (var stream = request.GetRequestStream())
            {
                stream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                return reader.ReadToEnd();
            }
        }

        #endregion

        #region JSON / Message Helpers

        private static string EscapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static string ExtractMessagePart(Models.OutboundPayload payload, string rawJson, string partName)
        {
            if (payload.Message != null && payload.Message.ContainsKey(partName))
            {
                var val = payload.Message[partName];
                if (!string.IsNullOrEmpty(val)) return val;
            }
            return ExtractJsonField(rawJson, "Message", partName);
        }

        private static string ExtractJsonField(string json, string parentKey, string childKey)
        {
            var parentIdx = json.IndexOf("\"" + parentKey + "\"");
            if (parentIdx < 0) return null;

            var key = "\"" + childKey + "\":\"";
            var key2 = "\"" + childKey + "\": \"";
            var idx = json.IndexOf(key, parentIdx);
            if (idx < 0) idx = json.IndexOf(key2, parentIdx);
            if (idx < 0) return null;

            var searchKey = idx == json.IndexOf(key, parentIdx) ? key : key2;
            var start = idx + searchKey.Length;
            var sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                if (json[i] == '\\' && i + 1 < json.Length) { sb.Append(json[i + 1]); i++; }
                else if (json[i] == '"') break;
                else sb.Append(json[i]);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static string ExtractField(string json, string fieldName)
        {
            var key = fieldName + ":\"";
            var key2 = fieldName + ": \"";
            var idx = json.IndexOf(key);
            if (idx < 0) idx = json.IndexOf(key2);
            if (idx < 0) return null;

            var start = json.IndexOf("\"", idx + fieldName.Length + 1) + 1;
            var end = json.IndexOf("\"", start);
            return end > start ? json.Substring(start, end - start) : null;
        }

        #endregion

        private Entity RetrieveChannelConfig(IOrganizationService orgService, ITracingService trace, string fromAddress)
        {
            var query = new Microsoft.Xrm.Sdk.Query.QueryExpression("msdyn_channelinstance")
            {
                ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("msdyn_extendedentityid"),
                Criteria = new Microsoft.Xrm.Sdk.Query.FilterExpression(Microsoft.Xrm.Sdk.Query.LogicalOperator.Or)
                {
                    Conditions =
                    {
                        new Microsoft.Xrm.Sdk.Query.ConditionExpression(
                            "msdyn_contactpoint", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, fromAddress),
                        new Microsoft.Xrm.Sdk.Query.ConditionExpression(
                            "msdyn_name", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, fromAddress)
                    }
                }
            };

            var results = orgService.RetrieveMultiple(query);
            if (results.Entities.Count == 0)
                throw new InvalidPluginExecutionException("No channel instance found for sender: " + fromAddress);

            var channelInstance = results.Entities[0];
            var extRef = channelInstance.GetAttributeValue<EntityReference>("msdyn_extendedentityid");
            if (extRef == null)
                throw new InvalidPluginExecutionException("Channel instance has no extended configuration.");

            trace.Trace("Extended entity: {0} / {1}", extRef.LogicalName, extRef.Id);

            return orgService.Retrieve(extRef.LogicalName, extRef.Id,
                new Microsoft.Xrm.Sdk.Query.ColumnSet("new_apikey", "new_secret", "new_provider", "new_webhookurl"));
        }
    }

    internal static class JsonHelper
    {
        public static T Deserialize<T>(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)serializer.ReadObject(stream);
        }

        public static string Serialize<T>(T obj)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, obj);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
