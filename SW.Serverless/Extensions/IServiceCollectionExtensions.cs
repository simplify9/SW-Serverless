
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Resident;
using System;
using System.Reflection;

namespace SW.Serverless
{
    public static class IServiceCollectionExtensions
    {
        public static IServiceCollection AddServerless(this IServiceCollection services, Action<ServerlessOptions> configure = null)
        {
            var serverlessOptions = new ServerlessOptions();
            if (configure != null) configure.Invoke(serverlessOptions);
            services.AddSingleton(serverlessOptions);
            services.AddTransient<IServerlessService, ServerlessService>();
            services.AddMemoryCache();
            services.TryAddSingleton<AdapterInstaller>();

            return services;
        }

        /// <summary>
        /// Adds the resident adapter runtime: a Kestrel endpoint on a Unix domain socket or named
        /// pipe that adapters dial, plus the supervisor that owns their processes.
        /// Classic per-invocation adapters are untouched by this — see the design doc, section 15.
        /// </summary>
        public static IServiceCollection AddResidentAdapters<TSink>(this IServiceCollection services,
            Action<ResidentOptions> configure = null)
            where TSink : class, IAdapterEventSink
        {
            var options = new ResidentOptions();
            configure?.Invoke(options);

            services.AddMemoryCache();

            services.AddSingleton(options);
            services.TryAddSingleton<IAdapterEventSink, TSink>();

            // Replaceable, and a real deployment must replace it: the in-memory store is per
            // process, so a cursor saved on one node is invisible to the next one to run the
            // adapter. TryAdd, so a host that registered its own keeps it.
            services.TryAddSingleton<IAdapterStateStore, InMemoryAdapterStateStore>();
            services.TryAddSingleton<AdapterInstaller>();
            services.TryAddSingleton<IResidentAdapterLocator, DefaultResidentAdapterLocator>();
            services.AddSingleton<ResidentAdapterHost>();
            services.AddSingleton<IResidentAdapterHost>(sp => sp.GetRequiredService<ResidentAdapterHost>());
            services.AddHostedService(sp => sp.GetRequiredService<ResidentAdapterHost>());

            return services;
        }

        /// <summary>
        /// As <see cref="AddResidentAdapters{TSink}"/>, with the host's own durable state store —
        /// what a polling receiver's cursor is written to. Use this one anywhere state has to
        /// outlive the process or be visible to another node.
        /// </summary>
        public static IServiceCollection AddResidentAdapters<TSink, TStateStore>(
            this IServiceCollection services, Action<ResidentOptions> configure = null)
            where TSink : class, IAdapterEventSink
            where TStateStore : class, IAdapterStateStore
        {
            services.TryAddSingleton<IAdapterStateStore, TStateStore>();
            return services.AddResidentAdapters<TSink>(configure);
        }
    }
}
