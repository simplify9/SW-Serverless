using Microsoft.Extensions.Logging;
using SW.Serverless.Sdk.Resident;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SW.Serverless.Sdk.Hosting
{
    /// <summary>
    /// Routes ordinary <c>ILogger</c> calls onto the adapter's own log channel, so anything the
    /// adapter or its libraries log — HttpClient, Polly, your own services — reaches the host
    /// without the author thinking about it.
    ///
    /// Resident adapters get structured log frames with the logger name and any state as
    /// properties. Classic adapters fall back to AdapterLogger's stderr lines.
    /// </summary>
    public class AdapterLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        readonly IAdapterContext context;
        readonly ConcurrentDictionary<string, ILogger> loggers = new();
        IExternalScopeProvider scopeProvider;

        public AdapterLoggerProvider(IAdapterContext context = null) => this.context = context;

        public ILogger CreateLogger(string categoryName) =>
            loggers.GetOrAdd(categoryName, name => new AdapterLoggerAdapter(name, context, () => scopeProvider));

        public void SetScopeProvider(IExternalScopeProvider provider) => scopeProvider = provider;

        public void Dispose() => loggers.Clear();

        sealed class AdapterLoggerAdapter : ILogger
        {
            readonly string category;
            readonly IAdapterContext context;
            readonly Func<IExternalScopeProvider> scopes;

            public AdapterLoggerAdapter(string category, IAdapterContext context,
                Func<IExternalScopeProvider> scopes)
            {
                this.category = category;
                this.context = context;
                this.scopes = scopes;
            }

            public IDisposable BeginScope<TState>(TState state) =>
                scopes()?.Push(state) ?? NullScope.Instance;

            public bool IsEnabled(LogLevel level) =>
                level != LogLevel.None &&
                (context == null || (int)level >= (int)context.MinimumLogLevel);

            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(level)) return;

                var message = formatter(state, exception);

                if (context == null)
                {
                    // Classic lifecycle: stderr, the only channel v1 has.
                    switch (level)
                    {
                        case LogLevel.Critical:
                        case LogLevel.Error: AdapterLogger.LogError(exception, $"{category}: {message}"); break;
                        case LogLevel.Warning: AdapterLogger.LogWarning(exception, $"{category}: {message}"); break;
                        default: AdapterLogger.LogInformation(exception, $"{category}: {message}"); break;
                    }
                    return;
                }

                var properties = new Dictionary<string, string> { ["logger"] = category };
                if (eventId.Id != 0) properties["eventId"] = eventId.ToString();

                // Structured state survives as properties rather than being flattened into text.
                if (state is IEnumerable<KeyValuePair<string, object>> pairs)
                    foreach (var pair in pairs)
                        if (pair.Key != "{OriginalFormat}")
                            properties[pair.Key] = pair.Value?.ToString();

                scopes()?.ForEachScope((scope, _) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object>> scopePairs)
                        foreach (var pair in scopePairs)
                            properties[pair.Key] = pair.Value?.ToString();
                }, (object)null);

                context.Log((AdapterLogLevel)(int)level, message, exception, properties);
            }
        }

        sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
