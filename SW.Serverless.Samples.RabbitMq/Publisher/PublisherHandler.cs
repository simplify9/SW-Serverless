using RabbitMQ.Client;
using SW.Serverless.Sdk.Resident;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.RabbitMq.Publisher
{
    /// <summary>
    /// EGRESS. Publishes to a RabbitMQ exchange on a timer — 10 ms by default, so roughly 100
    /// messages a second, enough to make throughput and backpressure visible rather than
    /// theoretical.
    ///
    /// This is the direction Bitween does not have yet even on the internal gateway. The adapter
    /// owns the connection and the confirm handling; the host only says what to send.
    /// </summary>
    public class PublisherHandler : RabbitAdapterBase
    {
        IModel channel;
        Task loop;

        int intervalMs = 10;
        bool useConfirms = true;

        long published, confirmed, returned, failed;
        long sequence;
        DateTimeOffset? lastPublishedOn;
        readonly Stopwatch since = Stopwatch.StartNew();

        protected override void OnStarted()
        {
            if (int.TryParse(Context.StartupValueOf("IntervalMs"), out var ms) && ms > 0) intervalMs = ms;
            useConfirms = Context.StartupValueOf("PublisherConfirms") != "false";

            channel = Connection.CreateModel();

            if (useConfirms)
            {
                // Without confirms a publish is fire-and-forget: the broker may drop it and the
                // publisher never finds out. This is the RabbitMQ-native durability control, and
                // it belongs to the provider rather than to any host abstraction.
                channel.ConfirmSelect();
                channel.BasicAcks += (_, _) => Interlocked.Increment(ref confirmed);
                channel.BasicNacks += (_, e) =>
                {
                    Interlocked.Increment(ref failed);
                    LastError = $"Broker nacked delivery tag {e.DeliveryTag}.";
                };
            }

            // mandatory=true plus this handler is how you learn a message was unroutable
            // instead of silently discarding it.
            channel.BasicReturn += (_, e) =>
            {
                Interlocked.Increment(ref returned);
                LastError = $"Unroutable: {e.ReplyText} (routing key '{e.RoutingKey}').";
            };

            loop = Task.Run(() => PublishLoopAsync(Stopping.Token));
        }

        protected override void OnStopping()
        {
            try { loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
            try { channel?.Close(); channel?.Dispose(); } catch { }
        }

        async Task PublishLoopAsync(CancellationToken ct)
        {
            var properties = channel.CreateBasicProperties();
            properties.ContentType = "application/json";
            properties.DeliveryMode = 1;   // transient: this is a load generator, not a ledger

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var n = Interlocked.Increment(ref sequence);
                    var body = Encoding.UTF8.GetBytes(
                        $"{{\"sequence\":{n},\"utc\":\"{DateTimeOffset.UtcNow:O}\",\"source\":\"{Context.InstanceKey}\"}}");

                    properties.MessageId = $"{Context.InstanceKey}-{n}";
                    properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                    lock (channel)
                        channel.BasicPublish(ExchangeName, RoutingKey, mandatory: true, properties, body);

                    Interlocked.Increment(ref published);
                    lastPublishedOn = DateTimeOffset.UtcNow;

                    if (published % 100 == 0)
                        Context.Metric("rabbit.published", 100,
                            new Dictionary<string, string> { ["exchange"] = ExchangeName });
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    LastError = ex.Message;
                    Context.LogError("Publish failed.", ex);
                    State = "Faulted";
                }

                try { await Task.Delay(intervalMs, ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        protected override void Describe(AdapterStatus status)
        {
            status.Details["published"] = published.ToString();
            status.Details["confirmed"] = confirmed.ToString();
            status.Details["returned"] = returned.ToString();
            status.Details["failed"] = failed.ToString();
            status.Details["intervalMs"] = intervalMs.ToString();
            status.Details["ratePerSecond"] = since.Elapsed.TotalSeconds > 0
                ? (published / since.Elapsed.TotalSeconds).ToString("0.0")
                : "0";
            status.LastMessageOn = lastPublishedOn;
        }

        // ------------------------------------------------------------------ commands

        /// <summary>Change the publish rate while running. No restart, no redeploy.</summary>
        public Task<object> SetInterval(int milliseconds)
        {
            if (milliseconds is < 1 or > 60000)
                throw new ArgumentOutOfRangeException(nameof(milliseconds), "Expected 1..60000 ms.");

            intervalMs = milliseconds;
            Context.LogInformation($"Publish interval changed to {milliseconds} ms at runtime.");
            return Task.FromResult<object>(new { intervalMs, approxPerSecond = 1000 / milliseconds });
        }

        /// <summary>Publish one message on demand — the host driving egress explicitly.</summary>
        public Task<object> PublishOne(string body)
        {
            var properties = channel.CreateBasicProperties();
            properties.ContentType = "application/json";
            properties.MessageId = $"{Context.InstanceKey}-manual-{Guid.NewGuid():N}";

            var payload = Encoding.UTF8.GetBytes(body ?? "{}");
            lock (channel)
                channel.BasicPublish(ExchangeName, RoutingKey, mandatory: true, properties, payload);

            Interlocked.Increment(ref published);
            return Task.FromResult<object>(new { messageId = properties.MessageId, bytes = payload.Length });
        }

        /// <summary>Publish to a routing key nothing is bound to, to demonstrate BasicReturn.</summary>
        public Task<object> PublishUnroutable()
        {
            var properties = channel.CreateBasicProperties();
            var key = $"nobody.listens.{Guid.NewGuid():N}";

            lock (channel)
                channel.BasicPublish(ExchangeName, key, mandatory: true, properties,
                    Encoding.UTF8.GetBytes("{\"unroutable\":true}"));

            return Task.FromResult<object>(new
            {
                routingKey = key,
                note = "mandatory=true, so the broker returns it. Watch 'returned' on the status."
            });
        }

        public Task<object> GetStats() => Task.FromResult<object>(new
        {
            published, confirmed, returned, failed, intervalMs,
            confirmsEnabled = useConfirms,
            uptimeSeconds = (int)since.Elapsed.TotalSeconds
        });
    }
}
