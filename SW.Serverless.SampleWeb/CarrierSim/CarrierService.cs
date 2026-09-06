using Grpc.Core;
using Microsoft.Extensions.Logging;
using SW.Serverless.Samples.CarrierContract;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.SampleWeb.CarrierSim
{
    /// <summary>
    /// A stand-in for a real carrier's gRPC API, so the adapter has something genuine to call.
    /// It deliberately misbehaves in the ways real carriers do — variable latency, a rejection
    /// for oversized parcels, and a configurable failure rate — because an adapter that is only
    /// ever tested against a perfect upstream teaches you nothing.
    /// </summary>
    public class CarrierService : Carrier.CarrierBase
    {
        // gRPC services are resolved per call, so instance state would vanish between them.
        static readonly ConcurrentDictionary<string, ShipmentRecord> shipments = new();

        // Reference -> tracking number. Real carriers offer exactly this so a client that retries
        // a timed-out create does not end up with two shipments; without it, no caller can safely
        // retry CreateShipment at all.
        static readonly ConcurrentDictionary<string, ShipmentRecord> byReference = new();
        readonly ILogger<CarrierService> logger;

        public CarrierService(ILogger<CarrierService> logger) => this.logger = logger;

        /// <summary>0..100. Raised from the dashboard to show how an adapter surfaces upstream failure.</summary>
        public static int FailurePercent;

        /// <summary>Simulated upstream latency in milliseconds.</summary>
        public static int LatencyMs = 40;

        public override async Task<CreateShipmentReply> CreateShipment(
            CreateShipmentRequest request, ServerCallContext context)
        {
            await SimulateAsync(context);

            if (string.IsNullOrWhiteSpace(request.Account))
                return new CreateShipmentReply
                {
                    Accepted = false,
                    ErrorCode = "INVALID_ACCOUNT",
                    ErrorMessage = "No account number was supplied."
                };

            if (request.Parcels.Count == 0)
                return new CreateShipmentReply
                {
                    Accepted = false,
                    ErrorCode = "NO_PARCELS",
                    ErrorMessage = "At least one parcel is required."
                };

            var invalid = request.Parcels.FirstOrDefault(
                p => double.IsNaN(p.WeightKg) || double.IsInfinity(p.WeightKg) || p.WeightKg <= 0);
            if (invalid != null)
                return new CreateShipmentReply
                {
                    Accepted = false,
                    ErrorCode = "INVALID_WEIGHT",
                    ErrorMessage = $"{invalid.WeightKg} kg is not a usable parcel weight."
                };

            var overweight = request.Parcels.FirstOrDefault(p => p.WeightKg > 31.5);
            if (overweight != null)
                return new CreateShipmentReply
                {
                    Accepted = false,
                    ErrorCode = "PARCEL_TOO_HEAVY",
                    ErrorMessage = $"{overweight.WeightKg} kg exceeds the 31.5 kg limit for this service."
                };

            var price = Math.Round(4.50 + request.Parcels.Sum(p => p.WeightKg) * 0.85, 2);

            var record = new ShipmentRecord
            {
                Reference = request.Reference,
                CreatedOn = DateTimeOffset.UtcNow,
                Destination = request.Recipient?.City ?? "unknown"
            };

            // A repeat of a reference we already booked returns the original shipment rather than
            // creating a second one. This is what makes the adapter's retry safe.
            if (!string.IsNullOrWhiteSpace(request.Reference))
            {
                var winner = byReference.GetOrAdd(request.Reference, record);
                if (!ReferenceEquals(winner, record))
                {
                    logger.LogInformation("Carrier replayed {Reference} as {Tracking}.",
                        request.Reference, winner.TrackingNumber);
                    return new CreateShipmentReply
                    {
                        Accepted = true,
                        TrackingNumber = winner.TrackingNumber,
                        LabelUrl = $"https://carrier.test/labels/{winner.TrackingNumber}.pdf",
                        Price = winner.Price,
                        Currency = "EUR"
                    };
                }
            }

            // TryAdd rather than the indexer: a repeated random number would otherwise replace an
            // existing shipment, and tracking and cancellation would then act on the wrong one.
            string tracking;
            while (true)
            {
                tracking = $"SW{Random.Shared.NextInt64(100000000, 999999999)}";
                if (shipments.TryAdd(tracking, record)) break;
            }

            record.TrackingNumber = tracking;
            record.Price = price;

            logger.LogInformation("Carrier accepted {Reference} as {Tracking} for {Price} EUR.",
                request.Reference, tracking, price);

            return new CreateShipmentReply
            {
                Accepted = true,
                TrackingNumber = tracking,
                LabelUrl = $"https://carrier.test/labels/{tracking}.pdf",
                Price = price,
                Currency = "EUR"
            };
        }

        public override async Task<TrackReply> Track(TrackRequest request, ServerCallContext context)
        {
            await SimulateAsync(context);

            if (!shipments.TryGetValue(request.TrackingNumber ?? "", out var record))
                return new TrackReply { Found = false, TrackingNumber = request.TrackingNumber ?? "" };

            var age = DateTimeOffset.UtcNow - record.CreatedOn;
            var reply = new TrackReply { Found = true, TrackingNumber = request.TrackingNumber };

            // Traces accumulate with age, so tracking the same shipment twice shows movement.
            Add(reply, record, "COLLECTED", "Collected from sender", TimeSpan.Zero);
            if (age > TimeSpan.FromSeconds(10))
                Add(reply, record, "IN_TRANSIT", "Departed sorting hub", TimeSpan.FromSeconds(10));
            if (age > TimeSpan.FromSeconds(25))
                Add(reply, record, "OUT_FOR_DELIVERY", "With the courier", TimeSpan.FromSeconds(25));
            if (age > TimeSpan.FromSeconds(45))
                Add(reply, record, "DELIVERED", "Signed for", TimeSpan.FromSeconds(45));

            reply.CurrentStatus = reply.Traces.Last().Status;
            return reply;
        }

        void Add(TrackReply reply, ShipmentRecord record, string status, string description, TimeSpan after) =>
            reply.Traces.Add(new Trace
            {
                Status = status,
                Description = description,
                Location = record.Destination,
                OccurredUnixMs = record.CreatedOn.Add(after).ToUnixTimeMilliseconds()
            });

        public override async Task<CancelReply> CancelShipment(CancelRequest request, ServerCallContext context)
        {
            await SimulateAsync(context);

            return shipments.TryRemove(request.TrackingNumber ?? "", out _)
                ? new CancelReply { Cancelled = true }
                : new CancelReply { Cancelled = false, ErrorMessage = "Unknown tracking number." };
        }

        public override Task<PingReply> Ping(PingRequest request, ServerCallContext context) =>
            Task.FromResult(new PingReply
            {
                Version = "carrier-sim/1.0",
                ServerTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

        static async Task SimulateAsync(ServerCallContext context)
        {
            if (LatencyMs > 0)
                await Task.Delay(Random.Shared.Next(LatencyMs / 2, LatencyMs * 2),
                    context.CancellationToken);

            if (FailurePercent > 0 && Random.Shared.Next(100) < FailurePercent)
                throw new RpcException(new Status(StatusCode.Unavailable,
                    "The carrier's API is temporarily unavailable."));
        }

        sealed class ShipmentRecord
        {
            public string Reference;
            public DateTimeOffset CreatedOn;
            public string Destination;
            public string TrackingNumber;
            public double Price;
        }
    }
}
