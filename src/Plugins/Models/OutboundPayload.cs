using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace WhatsAppACSChannel.Plugins.Models
{
    [DataContract]
    public class OutboundPayload
    {
        [DataMember(Name = "ChannelDefinitionId")]
        public Guid ChannelDefinitionId { get; set; }

        [DataMember(Name = "RequestId")]
        public string RequestId { get; set; }

        [DataMember(Name = "IdempotencyId")]
        public string IdempotencyId { get; set; }

        [DataMember(Name = "From")]
        public string From { get; set; }

        [DataMember(Name = "To")]
        public string To { get; set; }

        [DataMember(Name = "Message")]
        public Dictionary<string, string> Message { get; set; }

        [DataMember(Name = "MarketingAppContext")]
        public MarketingContext MarketingAppContext { get; set; }
    }

    [DataContract]
    public class MarketingContext
    {
        [DataMember(Name = "CustomerJourneyId")]
        public string CustomerJourneyId { get; set; }

        [DataMember(Name = "UserId")]
        public Guid? UserId { get; set; }

        [DataMember(Name = "UserEntityType")]
        public string UserEntityType { get; set; }

        [DataMember(Name = "IsTestSend")]
        public bool IsTestSend { get; set; }
    }

    [DataContract]
    public class OutboundResponse
    {
        [DataMember(Name = "ChannelDefinitionId")]
        public Guid ChannelDefinitionId { get; set; }

        [DataMember(Name = "MessageId")]
        public string MessageId { get; set; }

        [DataMember(Name = "RequestId")]
        public string RequestId { get; set; }

        [DataMember(Name = "Status")]
        public string Status { get; set; }
    }
}
