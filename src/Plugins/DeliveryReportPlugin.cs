using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Xrm.Sdk;

namespace WhatsAppACSChannel.Plugins
{
    /// <summary>
    /// Plugin to process WhatsApp delivery reports received from ACS webhooks.
    /// This plugin calls the msdyn_D365ChannelsNotification API to report delivery status.
    /// 
    /// Register this plugin on a Custom API (e.g., new_WhatsAppDeliveryReport) that
    /// your Azure Function / webhook calls when ACS sends delivery status updates.
    /// </summary>
    public class DeliveryReportPlugin : IPlugin
    {
        // Must match the channel definition ID in customizations.xml
        private static readonly Guid ChannelDefinitionId = new Guid("b8f40227-a3bc-4e5d-9f6a-1c2d3e4f5a6b");

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var orgServiceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var orgService = orgServiceFactory.CreateOrganizationService(context.UserId);

            try
            {
                // Extract input parameters from the custom API call
                var notificationJson = (string)context.InputParameters["notificationPayload"];
                tracingService.Trace($"Received delivery report: {notificationJson}");

                var notification = Deserialize<DeliveryNotification>(notificationJson);

                // Build the payload for msdyn_D365ChannelsNotification
                var d365Notification = new D365ChannelNotification
                {
                    ChannelDefinitionId = ChannelDefinitionId,
                    RequestId = notification.RequestId,
                    MessageId = notification.MessageId,
                    From = notification.From,
                    OrganizationId = notification.OrganizationId,
                    Status = notification.Status, // "Delivered" or "NotDelivered"
                    Details = notification.Details
                };

                var payload = Serialize(d365Notification);
                tracingService.Trace($"Calling msdyn_D365ChannelsNotification with: {payload}");

                // Call the D365 base notification API
                var request = new OrganizationRequest("msdyn_D365ChannelsNotification");
                request["notificationPayLoad"] = payload;

                orgService.Execute(request);

                tracingService.Trace("Delivery report processed successfully.");
            }
            catch (Exception ex)
            {
                tracingService.Trace($"Error processing delivery report: {ex}");
                throw new InvalidPluginExecutionException($"Failed to process delivery report: {ex.Message}", ex);
            }
        }

        private static T Deserialize<T>(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        private static string Serialize<T>(T obj)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, obj);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        [DataContract]
        public class DeliveryNotification
        {
            [DataMember] public string RequestId { get; set; }
            [DataMember] public string MessageId { get; set; }
            [DataMember] public string From { get; set; }
            [DataMember] public string OrganizationId { get; set; }
            [DataMember] public string Status { get; set; } // "Delivered" or "NotDelivered"
            [DataMember] public DeliveryDetails Details { get; set; }
        }

        [DataContract]
        public class DeliveryDetails
        {
            [DataMember] public string Reason { get; set; }
            [DataMember] public string Message { get; set; }
        }

        [DataContract]
        public class D365ChannelNotification
        {
            [DataMember] public Guid ChannelDefinitionId { get; set; }
            [DataMember] public string RequestId { get; set; }
            [DataMember] public string MessageId { get; set; }
            [DataMember] public string From { get; set; }
            [DataMember] public string OrganizationId { get; set; }
            [DataMember] public string Status { get; set; }
            [DataMember] public DeliveryDetails Details { get; set; }
        }
    }
}
