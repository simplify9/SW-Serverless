using SW.Serverless.Sdk;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.Classic
{
    /// <summary>
    /// A conventional per-invocation adapter — the shape the ~190 published Traxis and Bitween
    /// adapters already use. Commands are public Task / Task&lt;T&gt; methods discovered by name;
    /// configuration comes from startup values; logging goes through AdapterLogger.
    /// </summary>
    [AdapterKind("handler")]
    [AdapterKind("mapper")]
    public class Handler
    {
        /// <summary>
        /// Process-static, deliberately. Under the classic lifecycle a process serves exactly one
        /// caller, so accumulating here is safe and is how Traxis's HTTP audit trail works — its
        /// GetLogs returns everything since process start, and multipiece shipments rely on that
        /// accumulation across several commands.
        ///
        /// It is also precisely why a POOLED resident adapter cannot simply reuse this process:
        /// one caller's entries would leak into the next. Pooling needs IResettable and an
        /// explicit session boundary. See the design doc, section 14.5.
        /// </summary>
        static readonly List<string> CallLog = new();

        public Handler()
        {
            // Declare expectations here, and ONLY declare them. They are answered over the
            // {{expected}} command and surfaced by the host as GetExpectedStartupValues(), which
            // is how a UI knows what to prompt for and which values are private.
            Runner.Expect("BaseUrl", "https://api.example.test");
            Runner.Expect("ApiKey", isPrivate: true);
            Runner.Expect("TimeoutSeconds", "30");

            Record("Handler constructed");
        }

        // Read startup values LAZILY, never in the constructor.
        //
        // `Runner.Run(new Handler())` constructs the handler before Runner has parsed argv, so
        // Runner.StartupValueOf(...) in a constructor throws and the process dies before it can
        // report anything — the host just sees the stream close. This is a live footgun in the
        // classic SDK; deferring the read is the fix.
        string BaseUrl => Runner.StartupValueOf("BaseUrl");
        int TimeoutSeconds => Runner.StartupValueOf<int>("TimeoutSeconds");

        static void Record(string entry)
        {
            lock (CallLog) CallLog.Add($"{DateTimeOffset.UtcNow:HH:mm:ss.fff}  {entry}");
        }

        // ------------------------------------------------------------------ the contrast

        /// <summary>
        /// Returns this process's identity. Call it twice from the dashboard: the pid changes
        /// every time, because every call is a new process. The resident adapters on the
        /// Adapters page keep the same pid for their whole life.
        /// </summary>
        public Task<object> WhoAmI()
        {
            var process = Process.GetCurrentProcess();
            Record("WhoAmI");

            return Task.FromResult<object>(new
            {
                processId = process.Id,
                startedUtc = process.StartTime.ToUniversalTime().ToString("O"),
                uptimeMs = (long)(DateTime.Now - process.StartTime).TotalMilliseconds,
                correlationId = Runner.CorrelationId,
                workingSetMb = process.WorkingSet64 / 1024 / 1024
            });
        }

        /// <summary>
        /// Everything this process has accumulated. Because the process is per-call, this only
        /// ever contains the current caller's entries — the property pooling would break.
        /// </summary>
        public Task<object> GetCallLog()
        {
            lock (CallLog) return Task.FromResult<object>(new { entries = CallLog.ToArray() });
        }

        // ------------------------------------------------------------------ ordinary work

        /// <summary>A mapper-shaped command: typed in, typed out.</summary>
        public Task<OrderSummary> Summarize(Order order)
        {
            if (order?.Lines == null || order.Lines.Count == 0)
                throw new ArgumentException("The order has no lines.", nameof(order));

            Record($"Summarize {order.Reference} ({order.Lines.Count} lines)");
            AdapterLogger.LogInformation($"Summarizing {order.Reference} against {BaseUrl}.");

            return Task.FromResult(new OrderSummary
            {
                Reference = order.Reference,
                LineCount = order.Lines.Count,
                TotalQuantity = order.Lines.Sum(l => l.Quantity),
                TotalValue = Math.Round(order.Lines.Sum(l => l.Quantity * l.UnitPrice), 2),
                Currency = order.Currency ?? "EUR"
            });
        }

        public Task<string> Echo(string input)
        {
            Record($"Echo '{input}'");
            return Task.FromResult(input);
        }

        /// <summary>Shows a startup value being read — and that a private one is never echoed back.</summary>
        public Task<object> Configuration()
        {
            var apiKey = Runner.StartupValueOf("ApiKey");
            Record("Configuration");

            return Task.FromResult<object>(new
            {
                baseUrl = BaseUrl,
                timeoutSeconds = TimeoutSeconds,
                apiKeyConfigured = !string.IsNullOrEmpty(apiKey),
                apiKeyLength = apiKey?.Length ?? 0
            });
        }

        /// <summary>An exception here comes back as a faulted task on the host, not a hang.</summary>
        public Task Fail()
        {
            AdapterLogger.LogError(null, "Fail was called deliberately.");
            throw new InvalidOperationException("The carrier rejected the request: INVALID_ACCOUNT.");
        }

        /// <summary>
        /// Blocks, so a caller can hit CommandTimeout. Under the classic protocol a timeout does
        /// NOT kill this process, and the late reply is what the correlation fix on the host now
        /// discards — see the design doc, section 14.3.
        /// </summary>
        public async Task<object> Slow(int seconds)
        {
            Record($"Slow({seconds})");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            return new { sleptSeconds = seconds };
        }

        // ------------------------------------------------------------------ contracts
        // In a real deployment these live in the HOST's SDK — SimplyWorks.TraxisGateway.Sdk,
        // SW.Bitween.Sdk — never in SW.Serverless, which only ever sees opaque payloads.

        public class Order
        {
            public string Reference { get; set; }
            public string Currency { get; set; }
            public List<OrderLine> Lines { get; set; } = new();
        }

        public class OrderLine
        {
            public string Sku { get; set; }
            public int Quantity { get; set; }
            public decimal UnitPrice { get; set; }
        }

        public class OrderSummary
        {
            public string Reference { get; set; }
            public int LineCount { get; set; }
            public int TotalQuantity { get; set; }
            public decimal TotalValue { get; set; }
            public string Currency { get; set; }
        }
    }
}
