using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;

namespace SW.Serverless.SampleWeb.Telemetry
{
    /// <summary>
    /// Adapter log frames arrive as ordinary ILogger entries under
    /// "serverless.adapters.{adapterId}" — so they already land wherever the host's logs land.
    /// This provider additionally tees them into the dashboard.
    /// </summary>
    [ProviderAlias("AdapterCapture")]
    public class AdapterLogCaptureProvider : ILoggerProvider
    {
        const string Prefix = "serverless.adapters.";

        readonly DashboardState state;
        readonly ConcurrentDictionary<string, ILogger> loggers = new();

        public AdapterLogCaptureProvider(DashboardState state) => this.state = state;

        public ILogger CreateLogger(string categoryName) =>
            loggers.GetOrAdd(categoryName, name => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                ? new CaptureLogger(state, name[Prefix.Length..])
                : NullLogger.Instance);

        public void Dispose() => loggers.Clear();

        sealed class CaptureLogger : ILogger
        {
            readonly DashboardState state;
            readonly string adapterId;

            public CaptureLogger(DashboardState state, string adapterId)
            {
                this.state = state;
                this.adapterId = adapterId;
            }

            public IDisposable BeginScope<TState>(TState s) => null;
            public bool IsEnabled(LogLevel level) => true;

            public void Log<TState>(LogLevel level, EventId id, TState s, Exception exception,
                Func<TState, Exception, string> formatter) =>
                state.Logs.Add(new LogRow(DateTimeOffset.Now, adapterId, level.ToString(),
                    formatter(s, exception), exception?.Message));
        }

        sealed class NullLogger : ILogger
        {
            public static readonly NullLogger Instance = new();
            public IDisposable BeginScope<TState>(TState s) => null;
            public bool IsEnabled(LogLevel level) => false;
            public void Log<TState>(LogLevel l, EventId i, TState s, Exception e, Func<TState, Exception, string> f) { }
        }
    }
}
