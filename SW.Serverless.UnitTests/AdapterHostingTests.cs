using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Sdk.Hosting;
using System;
using System.Collections.Generic;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// The adapter-side DI container: configuration binding, options, logging and lifetimes.
    /// No process and no host involved — this is the builder on its own.
    /// </summary>
    [TestClass]
    public class AdapterHostingTests
    {
        public class DemoOptions
        {
            public string Path { get; set; }
            public int ChunkSizeKb { get; set; } = 256;
            public string Home { get; set; }
        }

        public interface IThing { string Value { get; } }

        public class Thing : IThing
        {
            public Thing(IOptions<DemoOptions> options) => Value = options.Value.Path;
            public string Value { get; }
        }

        public class DemoHandler
        {
            public DemoHandler(IThing thing, ILogger<DemoHandler> logger, IConfiguration configuration)
            {
                Thing = thing;
                Logger = logger;
                Configuration = configuration;
            }

            public IThing Thing { get; }
            public ILogger<DemoHandler> Logger { get; }
            public IConfiguration Configuration { get; }
        }

        static IServiceProvider Build(Dictionary<string, string> startupValues,
            Dictionary<string, string> adapterValues = null) =>
            AdapterHost.CreateBuilder()
                .ConfigureServices((configuration, services) =>
                {
                    services.Configure<DemoOptions>(configuration);
                    services.AddSingleton<IThing, Thing>();
                })
                .Build<DemoHandler>()
                .Build(null, startupValues, adapterValues ?? new Dictionary<string, string>());

        [TestMethod]
        public void Constructor_injection_works_and_options_bind_from_startup_values()
        {
            var provider = Build(new Dictionary<string, string>
            {
                ["Path"] = "/data/inbox",
                ["ChunkSizeKb"] = "512"
            });

            var handler = provider.GetRequiredService<DemoHandler>();

            Assert.AreEqual("/data/inbox", handler.Thing.Value);
            Assert.IsNotNull(handler.Logger);

            var options = provider.GetRequiredService<IOptions<DemoOptions>>().Value;
            Assert.AreEqual(512, options.ChunkSizeKb);
        }

        [TestMethod]
        public void Defaults_survive_when_a_startup_value_is_absent()
        {
            var options = Build(new Dictionary<string, string> { ["Path"] = "/x" })
                .GetRequiredService<IOptions<DemoOptions>>().Value;

            Assert.AreEqual(256, options.ChunkSizeKb, "an unset value must keep the property default");
        }

        /// <summary>
        /// Regression. Binding the environment unprefixed meant a startup value called Path bound
        /// to the machine's PATH and Home to HOME — the adapter read the environment instead of
        /// its own configuration, and the crash looked nothing like its cause. Only SWSL_-prefixed
        /// variables are bound now, and startup values still win over them.
        /// </summary>
        [TestMethod]
        public void The_machines_environment_cannot_shadow_a_startup_value()
        {
            var options = Build(new Dictionary<string, string> { ["Path"] = "/data/inbox" })
                .GetRequiredService<IOptions<DemoOptions>>().Value;

            Assert.AreEqual("/data/inbox", options.Path);
            Assert.AreNotEqual(Environment.GetEnvironmentVariable("PATH"), options.Path);
            Assert.IsNull(options.Home, "HOME must not bind to a property called Home");
        }

        [TestMethod]
        public void A_prefixed_environment_variable_is_bound_but_loses_to_a_startup_value()
        {
            // Captured and restored, not cleared: blanking a variable the developer's shell had
            // set would leak out of the test and change later behaviour.
            var previousChunk = Environment.GetEnvironmentVariable(AdapterConfiguration.EnvironmentPrefix + "ChunkSizeKb");
            var previousPath = Environment.GetEnvironmentVariable(AdapterConfiguration.EnvironmentPrefix + "Path");

            Environment.SetEnvironmentVariable(AdapterConfiguration.EnvironmentPrefix + "ChunkSizeKb", "64");
            Environment.SetEnvironmentVariable(AdapterConfiguration.EnvironmentPrefix + "Path", "/from-env");
            try
            {
                var options = Build(new Dictionary<string, string> { ["Path"] = "/from-startup" })
                    .GetRequiredService<IOptions<DemoOptions>>().Value;

                Assert.AreEqual(64, options.ChunkSizeKb, "the prefixed variable should be bound");
                Assert.AreEqual("/from-startup", options.Path, "startup values outrank the environment");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AdapterConfiguration.EnvironmentPrefix + "ChunkSizeKb", previousChunk);
                Environment.SetEnvironmentVariable(AdapterConfiguration.EnvironmentPrefix + "Path", previousPath);
            }
        }

        [TestMethod]
        public void Cloud_metadata_is_namespaced_so_it_cannot_shadow_configuration()
        {
            var provider = Build(
                new Dictionary<string, string> { ["Path"] = "/startup" },
                new Dictionary<string, string> { ["Path"] = "/metadata", ["Protocol"] = "2" });

            var configuration = provider.GetRequiredService<IConfiguration>();

            Assert.AreEqual("/startup", configuration["Path"]);
            Assert.AreEqual("2", configuration[$"{AdapterConfiguration.AdapterValuesSection}:Protocol"]);
        }

        [TestMethod]
        public void The_handler_is_a_singleton_and_scopes_are_validated()
        {
            var provider = Build(new Dictionary<string, string> { ["Path"] = "/x" });

            Assert.AreSame(
                provider.GetRequiredService<DemoHandler>(),
                provider.GetRequiredService<DemoHandler>());

            Assert.IsNotNull(provider.GetRequiredService<IServiceScopeFactory>(),
                "adapters need scoping for per-message work");
        }

        [TestMethod]
        public void A_session_is_ambient_and_nests_correctly()
        {
            Assert.IsFalse(AdapterSession.InSession);

            using (AdapterSession.Begin("1", "Outer"))
            {
                Assert.AreEqual("1", AdapterSession.Id);
                using (AdapterSession.Begin("2", "Inner"))
                    Assert.AreEqual("2", AdapterSession.Id);

                Assert.AreEqual("1", AdapterSession.Id, "the outer session must be restored");
            }

            Assert.IsFalse(AdapterSession.InSession);
        }
    }
}
