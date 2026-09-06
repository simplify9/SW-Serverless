using System;
using System.Collections.Generic;

namespace SW.Serverless.Samples.Carrier
{
    // The HOST's contract, not the carrier's. In a real deployment these types live in the host's
    // own SDK — SimplyWorks.TraxisGateway.Sdk — and the adapter's whole job is translating between
    // them and whatever the upstream happens to speak. SW.Serverless never sees either.

    public class ShipmentRequest
    {
        public string Reference { get; set; }
        public string Service { get; set; }
        public Party Sender { get; set; }
        public Party Recipient { get; set; }
        public List<Piece> Pieces { get; set; } = new();
    }

    public class Party
    {
        public string Name { get; set; }
        public string Street { get; set; }
        public string City { get; set; }
        public string PostCode { get; set; }
        public string Country { get; set; }
    }

    public class Piece
    {
        public double Weight { get; set; }
        public int Length { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class ShipmentResult
    {
        public bool Succeeded { get; set; }
        public string TrackingNumber { get; set; }
        public string LabelUrl { get; set; }
        public decimal Price { get; set; }
        public string Currency { get; set; }

        /// <summary>Set instead of throwing, so a carrier rejection is a result and not an outage.</summary>
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class TrackRequest
    {
        public string TrackingNumber { get; set; }
    }

    public class TrackResult
    {
        public bool Found { get; set; }
        public string TrackingNumber { get; set; }
        public string Status { get; set; }
        public List<TraceLine> Traces { get; set; } = new();
    }

    public class TraceLine
    {
        public string Status { get; set; }
        public string Description { get; set; }
        public string Location { get; set; }
        public DateTimeOffset OccurredOn { get; set; }
    }

    public class CallLogEntry
    {
        public DateTimeOffset On { get; set; }
        public string Operation { get; set; }
        public int Milliseconds { get; set; }
        public bool Succeeded { get; set; }
        public string Detail { get; set; }
    }

    public class CallLogResult
    {
        public string SessionId { get; set; }
        public List<CallLogEntry> Entries { get; set; } = new();
    }
}
