using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Resident
{
    public class InboundEvent
    {
        public string AdapterId { get; set; }
        public string InstanceKey { get; set; }
        public string Endpoint { get; set; }
        public string DedupeKey { get; set; }
        public string ContentType { get; set; }
        public byte[] Payload { get; set; }
        public System.Collections.Generic.IDictionary<string, string> Headers { get; set; }
        public string Traceparent { get; set; }
    }

    public class EventOutcome
    {
        public bool Accepted { get; set; }
        public string Reference { get; set; }
        public string Error { get; set; }

        public static EventOutcome Ok(string reference) =>
            new EventOutcome { Accepted = true, Reference = reference };

        public static EventOutcome Rejected(string error) =>
            new EventOutcome { Accepted = false, Error = error };
    }

    /// <summary>
    /// Implemented by the HOST APPLICATION. In Bitween this persists an Xchange and returns its id;
    /// the adapter does not acknowledge its broker until this has returned Accepted.
    /// </summary>
    public interface IAdapterEventSink
    {
        Task<EventOutcome> OnEventAsync(InboundEvent inboundEvent, CancellationToken cancellationToken);
    }
}
