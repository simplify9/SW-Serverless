using RabbitMQ.Client;
using SW.Serverless.Sdk.Resident;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.RabbitMq
{
    /// <summary>
    /// Connection handling shared by the two RabbitMQ samples.
    ///
    /// Two things are deliberate here. First, everything is a startup value — host, vhost,
    /// exchange type, queue arguments — because a provider must not be opinionated about the
    /// broker's own model. Second, reconnection is NOT attempted in a loop: the adapter reports
    /// itself disconnected and lets the supervisor decide, which is where restart policy,
    /// backoff and crash-loop quarantine already live.
    /// </summary>
    public abstract class RabbitAdapterBase : IResidentAdapter
    {
        protected IAdapterContext Context;
        protected IConnection Connection;
        protected CancellationTokenSource Stopping;

        protected string ExchangeName;
        protected string ExchangeType;
        protected string RoutingKey;

        volatile string state = "Starting";
        string lastError;

        protected string State { get => state; set => state = value; }
        protected string LastError { get => lastError; set => lastError = value; }

        public virtual Task StartAsync(IAdapterContext context, CancellationToken cancellationToken)
        {
            Context = context;
            Stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            ExchangeName = context.StartupValueOf("Exchange") ?? "swsl.sample";
            ExchangeType = context.StartupValueOf("ExchangeType") ?? RabbitMQ.Client.ExchangeType.Topic;
            RoutingKey = context.StartupValueOf("RoutingKey") ?? "sample.tick";

            Connection = CreateConnectionFactory(context).CreateConnection($"swsl-{context.InstanceKey}");
            Connection.ConnectionShutdown += (_, e) =>
            {
                State = "Disconnected";
                LastError = e.ReplyText;
                Context.LogWarning($"Connection closed: {e.ReplyCode} {e.ReplyText}");
            };

            using (var channel = Connection.CreateModel())
                DeclareExchange(channel);

            State = "Connected";
            context.LogInformation($"Connected to {Connection.Endpoint.HostName}:{Connection.Endpoint.Port}, " +
                                   $"exchange '{ExchangeName}' ({ExchangeType}).");

            OnStarted();
            return Task.CompletedTask;
        }

        protected virtual void OnStarted() { }

        protected static ConnectionFactory CreateConnectionFactory(IAdapterContext context)
        {
            // AMQP authenticates with PLAIN, so without TLS the broker password crosses the network
            // in the clear. Off by default only because the test broker is a local container;
            // set Tls=true (and Port=5671) for anything that is not localhost.
            var tls = context.StartupValueOf("Tls") == "true";
            var host = context.StartupValueOf("Host") ?? "localhost";

            var factory = new ConnectionFactory
            {
                HostName = host,
                Port = int.TryParse(context.StartupValueOf("Port"), out var port) ? port : (tls ? 5671 : 5672),
                UserName = context.StartupValueOf("UserName") ?? "guest",
                Password = context.StartupValueOf("Password") ?? "guest",
                VirtualHost = context.StartupValueOf("VirtualHost") ?? "/",

                // The supervisor owns restart policy, so do not also run a hidden one in here.
                AutomaticRecoveryEnabled = false,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(10)
            };

            if (tls)
            {
                factory.Ssl.Enabled = true;
                factory.Ssl.ServerName = context.StartupValueOf("TlsServerName") ?? host;
            }
            else if (host != "localhost" && host != "127.0.0.1")
            {
                context.LogWarning($"Connecting to '{host}' without TLS — the broker password will " +
                                   "cross the network in the clear. Set Tls=true.");
            }

            return factory;
        }

        protected void DeclareExchange(IModel channel) =>
            channel.ExchangeDeclare(ExchangeName, ExchangeType, durable: false, autoDelete: false);

        public virtual Task StopAsync(CancellationToken cancellationToken)
        {
            State = "Draining";
            Stopping?.Cancel();
            OnStopping();

            try { Connection?.Close(TimeSpan.FromSeconds(3)); } catch { }
            Connection?.Dispose();

            State = "Stopped";
            Context?.LogInformation("Disconnected from the broker.");
            return Task.CompletedTask;
        }

        protected virtual void OnStopping() { }

        public Task<AdapterStatus> GetStatusAsync()
        {
            var status = new AdapterStatus
            {
                Connected = Connection?.IsOpen == true,
                State = Connection?.IsOpen == true ? State : "Disconnected",
                LastError = LastError
            };

            status.Details["exchange"] = ExchangeName;
            status.Details["exchangeType"] = ExchangeType;
            status.Details["routingKey"] = RoutingKey;
            if (Connection != null)
                status.Details["endpoint"] = $"{Connection.Endpoint.HostName}:{Connection.Endpoint.Port}";

            Describe(status);
            return Task.FromResult(status);
        }

        protected abstract void Describe(AdapterStatus status);

        // ------------------------------------------------------------------ shared commands

        /// <summary>
        /// The control a UI needs before anything is saved. Staged, so a failure says WHICH step
        /// failed — reachable, authenticated, or authorised on the vhost.
        /// </summary>
        public Task<object> TestConnection()
        {
            var steps = new List<object>();
            IConnection probe = null;
            try
            {
                probe = CreateConnectionFactory(Context).CreateConnection("swsl-probe");
                steps.Add(new { step = "connect", ok = true, detail = probe.Endpoint.ToString() });

                using var channel = probe.CreateModel();
                steps.Add(new { step = "channel", ok = true, detail = "" });

                channel.ExchangeDeclarePassive(ExchangeName);
                steps.Add(new { step = "exchange", ok = true, detail = ExchangeName });

                return Task.FromResult<object>(new { ok = true, steps });
            }
            catch (Exception ex)
            {
                steps.Add(new { step = "failed", ok = false, detail = ex.Message });
                return Task.FromResult<object>(new { ok = false, steps });
            }
            finally
            {
                try { probe?.Close(); probe?.Dispose(); } catch { }
            }
        }

        /// <summary>Declare-if-absent, the shape the provider plan calls declareMode=create.</summary>
        public Task<object> DeclareTopology()
        {
            using var channel = Connection.CreateModel();
            DeclareExchange(channel);
            var extra = DeclareMore(channel);
            return Task.FromResult<object>(new { exchange = ExchangeName, type = ExchangeType, extra });
        }

        protected virtual object DeclareMore(IModel channel) => null;
    }
}
