
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

            services.AddSingleton(options);
            services.TryAddSingleton<IAdapterEventSink, TSink>();
            services.AddSingleton<ResidentAdapterHost>();
            services.AddSingleton<IResidentAdapterHost>(sp => sp.GetRequiredService<ResidentAdapterHost>());
            services.AddHostedService(sp => sp.GetRequiredService<ResidentAdapterHost>());

            return services;
        }
    }
}
