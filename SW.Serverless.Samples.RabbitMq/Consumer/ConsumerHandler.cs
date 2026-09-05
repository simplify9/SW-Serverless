using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SW.Serverless.Sdk.Resident;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.RabbitMq.Consumer
{
    /// <summary>
    /// INGRESS. Declares a queue, binds it to the exchange, consumes, and hands every message to
    /// the host.
    ///
    /// The ack ordering is the whole point, and it is the shape to copy for Kafka offsets too:
    ///
    ///     message arrives  ->  PublishAsync to the host  ->  host persists  ->  ack returns
    ///                      ->  ONLY THEN BasicAck
    ///
    /// If the host rejects, the message is nacked back onto the queue and redelivered. If this
    /// process dies between persisting and acking, the broker redelivers too — which is why every
    /// event carries a dedupe key rather than trusting delivery once.
    /// </summary>
    public class ConsumerHandler : RabbitAdapterBase
    {
        IModel channel;
        string consumerTag;
        string queueName;
        ushort prefetch = 16;

        long received, acked, nacked, failed;
        DateTimeOffset? lastMessageOn;

        protected override void OnStarted()
        {
            queueName = Context.StartupValueOf("Queue") ?? $"swsl.sample.{Context.InstanceKey}";
            if (ushort.TryParse(Context.StartupValueOf("Prefetch"), out var configured) && configured > 0)
                prefetch = configured;

            channel = Connection.CreateModel();

            DeclareQueueAndBinding(channel);

            // Prefetch is per channel and is the broker-side half of backpressure. The host-side
            // half is the credit window on PublishAsync; size them together.
            channel.BasicQos(0, prefetch, global: false);

            var consumer = new EventingBasicConsumer(channel);
            consumer.Received += OnReceived;

            // autoAck: false is not a detail — it is the entire reason this ordering is possible.
            consumerTag = channel.BasicConsume(queueName, autoAck: false, consumer);

            Context.LogInformation($"Consuming '{queueName}' with prefetch {prefetch}, tag {consumerTag}.");
        }

        void DeclareQueueAndBinding(IModel model)
        {
            var arguments = new Dictionary<string, object>();

            // Pass broker arguments straight through. A provider should not decide for the user
            // whether their queue is classic or quorum, or what its TTL is.
            if (Context.StartupValueOf("QueueType") is { Length: > 0 } queueType)
                arguments["x-queue-type"] = queueType;
            if (int.TryParse(Context.StartupValueOf("MessageTtlMs"), out var ttl) && ttl > 0)
                arguments["x-message-ttl"] = ttl;
            if (int.TryParse(Context.StartupValueOf("MaxLength"), out var maxLength) && maxLength > 0)
                arguments["x-max-length"] = maxLength;

            model.QueueDeclare(queueName,
                durable: Context.StartupValueOf("Durable") == "true",
                exclusive: false,
                autoDelete: Context.StartupValueOf("AutoDelete") != "false",
                arguments: arguments.Count == 0 ? null : arguments);

            model.QueueBind(queueName, ExchangeName, RoutingKey);
        }

        protected override void OnStopping()
        {
            try { if (consumerTag != null) channel?.BasicCancel(consumerTag); } catch { }
            try { channel?.Close(); channel?.Dispose(); } catch { }
        }

        void OnReceived(object sender, BasicDeliverEventArgs delivery)
        {
            // EventingBasicConsumer dispatches on the connection's consumer thread, so the async
            // work is offloaded rather than blocking further deliveries.
            _ = Task.Run(() => HandleAsync(delivery));
        }

        async Task HandleAsync(BasicDeliverEventArgs delivery)
        {
            Interlocked.Increment(ref received);

            try
            {
                var headers = new Dictionary<string, string>
                {
                    ["rabbit.exchange"] = delivery.Exchange,
                    ["rabbit.routingKey"] = delivery.RoutingKey,
                    ["rabbit.redelivered"] = delivery.Redelivered.ToString(),
                    ["rabbit.deliveryTag"] = delivery.DeliveryTag.ToString()
                };
                if (delivery.BasicProperties?.MessageId is { Length: > 0 } messageId)
                    headers["rabbit.messageId"] = messageId;

                var result = await Context.PublishAsync(
                    delivery.Body,
                    // The broker's message id is the natural dedupe key. Falling back to a
                    // delivery tag would be wrong: tags are per channel and reset on reconnect.
                    dedupeKey: delivery.BasicProperties?.MessageId is { Length: > 0 } id
                        ? $"rabbit:{ExchangeName}:{id}"
                        : $"rabbit:{ExchangeName}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(delivery.Body.Span))[..32]}",
                    endpoint: queueName,
                    headers: headers,
                    contentType: delivery.BasicProperties?.ContentType ?? "application/octet-stream",
                    cancellationToken: Stopping.Token);

                if (result.Accepted)
                {
                    channel.BasicAck(delivery.DeliveryTag, multiple: false);
                    Interlocked.Increment(ref acked);
                    lastMessageOn = DateTimeOffset.UtcNow;

                    if (acked % 100 == 0)
                        Context.Metric("rabbit.consumed", 100,
                            new Dictionary<string, string> { ["queue"] = queueName });
                }
                else
                {
                    // Back onto the queue. The host said no, so this is a redelivery, not a loss.
                    channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
                    Interlocked.Increment(ref nacked);
                    LastError = result.Error;
                }
            }
            catch (OperationCanceledException)
            {
                try { channel.BasicNack(delivery.DeliveryTag, false, requeue: true); } catch { }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                LastError = ex.Message;
                Context.LogError("Failed to hand a delivery to the host.", ex);
                try { channel.BasicNack(delivery.DeliveryTag, false, requeue: true); } catch { }
            }
        }

        protected override object DeclareMore(IModel model)
        {
            DeclareQueueAndBinding(model);
            return new { queue = queueName, boundTo = ExchangeName, routingKey = RoutingKey };
        }

        protected override void Describe(AdapterStatus status)
        {
            status.Details["queue"] = queueName;
            status.Details["prefetch"] = prefetch.ToString();
            status.Details["consumerTag"] = consumerTag ?? "";
            status.Details["received"] = received.ToString();
            status.Details["acked"] = acked.ToString();
            status.Details["nacked"] = nacked.ToString();
            status.Details["failed"] = failed.ToString();
            status.Details["depth"] = QueueDepth()?.ToString() ?? "?";

            status.InFlight = Math.Max(0, received - acked - nacked - failed);
            status.LastMessageOn = lastMessageOn;

            // "Connected but receiving nothing" is a real and different state from "idle".
            if (Connection?.IsOpen == true && received == 0) status.State = "Idle";
        }

        uint? QueueDepth()
        {
            try
            {
                // A passive declare is the cheap way to read depth without the management plugin.
                using var probe = Connection.CreateModel();
                return probe.QueueDeclarePassive(queueName).MessageCount;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ commands

        /// <summary>
        /// What is actually over there. With the management plugin this would list every exchange,
        /// queue and binding; on AMQP alone a passive declare still answers for what we know about.
        /// </summary>
        public Task<object> Discover()
        {
            uint depth = 0, consumers = 0;
            try
            {
                using var probe = Connection.CreateModel();
                var declared = probe.QueueDeclarePassive(queueName);
                depth = declared.MessageCount;
                consumers = declared.ConsumerCount;
            }
            catch (Exception ex)
            {
                return Task.FromResult<object>(new { error = ex.Message });
            }

            return Task.FromResult<object>(new
            {
                endpoint = $"{Connection.Endpoint.HostName}:{Connection.Endpoint.Port}",
                exchange = new { name = ExchangeName, type = ExchangeType },
                queue = new { name = queueName, messages = depth, consumers, prefetch },
                binding = new { from = ExchangeName, to = queueName, routingKey = RoutingKey }
            });
        }

        /// <summary>Change prefetch while running — the broker-side backpressure dial.</summary>
        public Task<object> SetPrefetch(int value)
        {
            if (value is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(value), "Expected 1..65535.");

            prefetch = (ushort)value;
            channel.BasicQos(0, prefetch, global: false);
            Context.LogInformation($"Prefetch changed to {prefetch} at runtime.");
            return Task.FromResult<object>(new { prefetch });
        }

        public Task<object> PurgeQueue()
        {
            var purged = channel.QueuePurge(queueName);
            Context.LogWarning($"Purged {purged} messages from '{queueName}'.");
            return Task.FromResult<object>(new { purged });
        }

        public Task<object> GetStats() => Task.FromResult<object>(new
        {
            queueName, prefetch, received, acked, nacked, failed,
            depth = QueueDepth(),
            inFlight = Math.Max(0, received - acked - nacked - failed)
        });
    }
}
