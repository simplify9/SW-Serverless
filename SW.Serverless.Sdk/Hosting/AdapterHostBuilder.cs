using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SW.Serverless.Sdk.Resident;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SW.Serverless.Sdk.Hosting
{
    public static class AdapterHost
    {
        /// <summary>
        /// Dependency injection for adapters, shaped like the host builder every .NET developer
        /// already knows:
        ///
        ///     static Task Main() =&gt; AdapterHost.CreateBuilder()
        ///         .ConfigureServices((config, services) =&gt;
        ///         {
        ///             services.AddHttpClient();
        ///             services.AddSingleton&lt;IPricer, Pricer&gt;();
        ///             services.Configure&lt;MyOptions&gt;(config);
        ///         })
        ///         .Build&lt;MyHandler&gt;()
        ///         .RunResidentAsync();
        ///
        /// You get for free: <c>ILogger&lt;T&gt;</c> routed to the host's logs,
        /// <c>IConfiguration</c> bound from startup values and cloud metadata, and
        /// <c>IAdapterContext</c> for pushing events and metrics.
        /// </summary>
        public static AdapterHostBuilder CreateBuilder() => new();
    }

    public class AdapterHostBuilder
    {
        readonly List<Action<IConfiguration, IServiceCollection>> configurators = new();

        internal AdapterHostBuilder() { }

        public AdapterHostBuilder ConfigureServices(Action<IServiceCollection> configure)
        {
            configurators.Add((_, services) => configure(services));
            return this;
        }

        /// <summary>Configuration here is startup values plus cloud metadata, already bound.</summary>
        public AdapterHostBuilder ConfigureServices(Action<IConfiguration, IServiceCollection> configure)
        {
            configurators.Add(configure);
            return this;
        }

        public AdapterHostBuilder ConfigureLogging(Action<ILoggingBuilder> configure)
        {
            configurators.Add((_, services) => services.AddLogging(configure));
            return this;
        }

        public AdapterHostRunner<THandler> Build<THandler>() where THandler : class =>
            new(configurators);
    }

    /// <summary>
    /// Owns the container. The handler is a singleton — it is the command target, and for a
    /// resident adapter it also holds the connection — and the container is built only once
    /// startup values have arrived, which is what makes constructor injection of configuration
    /// safe.
    /// </summary>
    public class AdapterHostRunner<THandler> where THandler : class
    {
        readonly List<Action<IConfiguration, IServiceCollection>> configurators;

        internal AdapterHostRunner(List<Action<IConfiguration, IServiceCollection>> configurators) =>
            this.configurators = configurators;

        ServiceProvider provider;

        /// <summary>Classic, per-invocation lifecycle.</summary>
        public async Task RunAsync()
        {
            try
            {
                await Runner.Run(() =>
                {
                    provider = (ServiceProvider)Build(null, Runner.StartupValues, Runner.AdapterValues);
                    return provider.GetRequiredService<THandler>();
                });
            }
            finally
            {
                // Singletons the adapter registered — an HttpClient factory, a broker connection —
                // get their Dispose called rather than relying on process exit.
                if (provider != null) await provider.DisposeAsync();
            }
        }

        /// <summary>Resident lifecycle: stays running, pushes events.</summary>
        public async Task RunResidentAsync()
        {
            try
            {
                await ResidentRunner.RunAsync(typeof(THandler), context =>
                {
                    provider = (ServiceProvider)Build(context, context.StartupValues, context.AdapterValues);
                    return provider.GetRequiredService<THandler>();
                });
            }
            finally
            {
                if (provider != null) await provider.DisposeAsync();
            }
        }

        /// <summary>Exposed for tests: build the container without running anything.</summary>
        public IServiceProvider Build(IAdapterContext context,
            IReadOnlyDictionary<string, string> startupValues,
            IReadOnlyDictionary<string, string> adapterValues)
        {
            var configuration = AdapterConfiguration.Build(startupValues, adapterValues);

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            if (context != null) services.AddSingleton(context);

            services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new AdapterLoggerProvider(context));

                // Trace here on purpose: the host decides the real level at runtime through
                // SetLogLevel, and IAdapterContext.MinimumLogLevel enforces it.
                logging.SetMinimumLevel(LogLevel.Trace);
            });

            services.TryAddSingleton<THandler>();

            foreach (var configure in configurators) configure(configuration, services);

            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = false
            });
        }
    }
}
