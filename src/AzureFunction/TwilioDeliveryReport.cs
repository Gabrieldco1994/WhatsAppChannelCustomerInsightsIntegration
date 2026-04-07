using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace TwilioWebhook
{
    /// <summary>
    /// Multi-provider delivery report endpoint.
    /// Auto-detects provider by payload format:
    ///   - Twilio: form-encoded with MessageSid/MessageStatus
    ///   - Infobip: JSON with results[].messageId and results[].status
    ///   - ACS: JSON with Event Grid format (stub)
    /// </summary>
    public class DeliveryReport
    {
        private readonly ILogger<DeliveryReport> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public DeliveryReport(ILogger<DeliveryReport> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        [Function("DeliveryReport")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            _logger.LogInformation("Delivery report received");

            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var contentType = req.Headers.GetValues("Content-Type")?.GetEnumerator();
                string ct = "";
                if (contentType != null && contentType.MoveNext())
                    ct = contentType.Current ?? "";

                _logger.LogInformation("Content-Type: {CT}, Body length: {Len}", ct, body.Length);

                List<DeliveryReportItem> items;

                if (ct.Contains("application/json") || body.TrimStart().StartsWith("{") || body.TrimStart().StartsWith("["))
                {
                    items = ParseInfobip(body);
                }
                else
                {
                    items = ParseTwilio(body);
                }

                foreach (var item in items)
                {
                    if (item.Status == null)
                    {
                        _logger.LogInformation("Intermediate status for {Sid}, skipping", item.MessageId);
                        continue;
                    }

                    _logger.LogInformation("Provider={Provider} SID={Sid} Status={Status} From={From} To={To}",
                        item.Provider, item.MessageId, item.Status, item.From, item.To);

                    await SendToD365(item);
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing delivery report");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                return errorResponse;
            }
        }

        // Keep old endpoint alive for backwards compatibility
        [Function("TwilioDeliveryReport")]
        public async Task<HttpResponseData> RunLegacy(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            return await Run(req);
        }

        #region Twilio Parser

        private List<DeliveryReportItem> ParseTwilio(string body)
        {
            _logger.LogInformation("Parsing as Twilio: {Body}", body);

            var formData = HttpUtility.ParseQueryString(body);
            var messageSid = formData["MessageSid"] ?? "";
            var messageStatus = formData["MessageStatus"] ?? "";
            var to = (formData["To"] ?? "").Replace("whatsapp:", "");
            var from = (formData["From"] ?? "").Replace("whatsapp:", "");
            var errorCode = formData["ErrorCode"];
            var errorMessage = formData["ErrorMessage"];

            string d365Status;
            switch (messageStatus.ToLower())
            {
                case "delivered":
                case "read":
                    d365Status = "Delivered";
                    break;
                case "failed":
                case "undelivered":
                    d365Status = "NotDelivered";
                    break;
                default:
                    return new List<DeliveryReportItem>
                    {
                        new DeliveryReportItem { MessageId = messageSid, Status = null, Provider = "Twilio" }
                    };
            }

            return new List<DeliveryReportItem>
            {
                new DeliveryReportItem
                {
                    Provider = "Twilio",
                    MessageId = messageSid,
                    Status = d365Status,
                    From = from,
                    To = to,
                    ErrorCode = errorCode ?? "",
                    ErrorMessage = errorMessage ?? messageStatus
                }
            };
        }

        #endregion

        #region Infobip Parser

        private List<DeliveryReportItem> ParseInfobip(string body)
        {
            _logger.LogInformation("Parsing as Infobip: {Body}", body);

            var items = new List<DeliveryReportItem>();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            JsonElement resultsArray;
            if (root.TryGetProperty("results", out resultsArray) && resultsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var result in resultsArray.EnumerateArray())
                {
                    var messageId = result.TryGetProperty("messageId", out var mid) ? mid.GetString() : "";
                    var to = result.TryGetProperty("to", out var toEl) ? toEl.GetString() : "";
                    var from = result.TryGetProperty("from", out var fromEl) ? fromEl.GetString() : "";

                    string groupName = "";
                    if (result.TryGetProperty("status", out var statusObj) && statusObj.ValueKind == JsonValueKind.Object)
                    {
                        groupName = statusObj.TryGetProperty("groupName", out var gn) ? gn.GetString() : "";
                    }

                    string errorCode = "";
                    string errorMessage = "";
                    if (result.TryGetProperty("error", out var errorObj) && errorObj.ValueKind == JsonValueKind.Object)
                    {
                        errorCode = errorObj.TryGetProperty("id", out var eid) ? eid.ToString() : "";
                        errorMessage = errorObj.TryGetProperty("description", out var edesc) ? edesc.GetString() : "";
                    }

                    string d365Status;
                    switch (groupName?.ToUpper())
                    {
                        case "DELIVERED":
                            d365Status = "Delivered";
                            break;
                        case "REJECTED":
                        case "UNDELIVERABLE":
                        case "EXPIRED":
                            d365Status = "NotDelivered";
                            break;
                        default:
                            items.Add(new DeliveryReportItem { MessageId = messageId, Status = null, Provider = "Infobip" });
                            continue;
                    }

                    items.Add(new DeliveryReportItem
                    {
                        Provider = "Infobip",
                        MessageId = messageId,
                        Status = d365Status,
                        From = from,
                        To = to,
                        ErrorCode = errorCode,
                        ErrorMessage = errorMessage
                    });
                }
            }

            if (items.Count == 0)
            {
                _logger.LogWarning("No results found in Infobip payload");
            }

            return items;
        }

        #endregion

        #region D365 Integration

        private async Task SendToD365(DeliveryReportItem item)
        {
            var channelDefId = Environment.GetEnvironmentVariable("CHANNEL_DEFINITION_ID");

            var notification = new
            {
                ChannelDefinitionId = channelDefId,
                RequestId = item.MessageId,
                MessageId = item.MessageId,
                From = item.From,
                OrganizationId = "",
                Status = item.Status,
                Details = new
                {
                    Reason = item.ErrorCode,
                    Message = item.ErrorMessage
                }
            };

            var notificationJson = JsonSerializer.Serialize(notification);
            _logger.LogInformation("Sending to D365 [{Provider}]: {Payload}", item.Provider, notificationJson);

            var d365Token = await GetD365TokenAsync();
            var d365Url = Environment.GetEnvironmentVariable("D365_URL")!.TrimEnd('/');
            var apiUrl = $"{d365Url}/api/data/v9.2/msdyn_D365ChannelsNotification";

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", d365Token);

            var requestPayload = new { notificationPayLoad = notificationJson };
            var content = new StringContent(
                JsonSerializer.Serialize(requestPayload),
                Encoding.UTF8, "application/json");

            var d365Response = await client.PostAsync(apiUrl, content);
            var d365Body = await d365Response.Content.ReadAsStringAsync();

            _logger.LogInformation("D365 response: {StatusCode} {Body}",
                d365Response.StatusCode, d365Body);
        }

        private async Task<string> GetD365TokenAsync()
        {
            var tenantId = Environment.GetEnvironmentVariable("D365_TENANT_ID");
            var clientId = Environment.GetEnvironmentVariable("D365_CLIENT_ID");
            var clientSecret = Environment.GetEnvironmentVariable("D365_CLIENT_SECRET");
            var d365Url = Environment.GetEnvironmentVariable("D365_URL")!.TrimEnd('/');

            var app = ConfidentialClientApplicationBuilder
                .Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                .Build();

            var result = await app.AcquireTokenForClient(
                new[] { $"{d365Url}/.default" }).ExecuteAsync();

            return result.AccessToken;
        }

        #endregion
    }

    internal class DeliveryReportItem
    {
        public string Provider { get; set; }
        public string MessageId { get; set; }
        public string Status { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
    }
}
