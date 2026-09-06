using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SW.PrimitiveTypes;
using SW.Serverless.UnitTests.Fixtures;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// Covers SW.Serverless.Samples.Classic — the non-resident lifecycle, unchanged by protocol 2.
    /// </summary>
    [TestClass]
    public class ClassicAdapterTests
    {
        const string AdapterId = "samples.classic";

        static TestServer server;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            server = new TestServer(WebHost.CreateDefaultBuilder()
                .UseDefaultServiceProvider((_, o) => o.ValidateScopes = true)
                .UseEnvironment(Environments.Development)
                .UseStartup<TestStartup>());

            await TestStore.PublishAsync(
                server.Host.Services.GetRequiredService<ICloudFilesService>(),
                AdapterId, "SW.Serverless.Samples.Classic",
                // No Protocol key on purpose: this must take the v1 path.
                new Dictionary<string, string> { ["Lifecycle"] = "invocation" });
        }

        [ClassCleanup]
        public static void ClassCleanup() => server?.Dispose();

        static IServerlessService Resolve() =>
            server.Host.Services.GetRequiredService<IServerlessService>();

        static readonly Dictionary<string, string> Startup = new()
        {
            ["BaseUrl"] = "https://api.test.invalid",
            ["ApiKey"] = "unit-test-key",
            ["TimeoutSeconds"] = "15"
        };

        /// <summary>
        /// The defining property of this lifecycle: one process per caller. If this ever returns
        /// the same pid twice, something is reusing processes and every adapter relying on
        /// process-static state is unsafe.
        /// </summary>
        [TestMethod]
        public async Task Every_invocation_gets_its_own_process()
        {
            var pids = new HashSet<int>();

            for (var i = 0; i < 3; i++)
            {
                var serverless = Resolve();
                await serverless.StartAsync(AdapterId, Guid.NewGuid().ToString("N"), Startup);

                var json = await serverless.InvokeAsync<string>("WhoAmI", null);
                pids.Add(JObject.Parse(json).Value<int>("processId"));

                ((IDisposable)serverless).Dispose();
            }

            Assert.AreEqual(3, pids.Count,
                "each classic invocation must run in its own process");
        }

        [TestMethod]
        public async Task Typed_input_and_output_round_trip()
        {
            var serverless = Resolve();
            await serverless.StartAsync(AdapterId, Guid.NewGuid().ToString("N"), Startup);

            var summary = await serverless.InvokeAsync<JObject>("Summarize", new
            {
                Reference = "SO-1",
                Currency = "EUR",
                Lines = new[] { new { Sku = "A", Quantity = 2, UnitPrice = 10.5m } }
            });

            Assert.AreEqual("SO-1", summary.Value<string>("Reference"));
            Assert.AreEqual(2, summary.Value<int>("TotalQuantity"));
            Assert.AreEqual(21.0m, summary.Value<decimal>("TotalValue"));
        }

        [TestMethod]
        public async Task Startup_values_reach_the_adapter()
        {
            var serverless = Resolve();
            await serverless.StartAsync(AdapterId, Guid.NewGuid().ToString("N"), Startup);

            var config = await serverless.InvokeAsync<JObject>("Configuration", null);

            Assert.AreEqual("https://api.test.invalid", config.Value<string>("baseUrl"));
            Assert.AreEqual(15, config.Value<int>("timeoutSeconds"));
            Assert.IsTrue(config.Value<bool>("apiKeyConfigured"));
        }

        [TestMethod]
        public async Task Expected_startup_values_describe_the_adapter()
        {
            var serverless = Resolve();
            await serverless.StartAsync(AdapterId, Guid.NewGuid().ToString("N"), Startup);

            var expected = await serverless.GetExpectedStartupValues();

            Assert.IsTrue(expected.ContainsKey("BaseUrl"));
            Assert.IsTrue(expected.ContainsKey("ApiKey"));
            Assert.IsTrue(expected["ApiKey"].Private, "ApiKey was declared private");
            Assert.AreEqual("30", expected["TimeoutSeconds"].Default);
        }

        [TestMethod]
        public async Task An_adapter_exception_faults_the_call()
        {
            var serverless = Resolve();
            await serverless.StartAsync(AdapterId, Guid.NewGuid().ToString("N"), Startup);

            var ex = await Assert.ThrowsExceptionAsync<Exception>(
                () => serverless.InvokeAsync("Fail", null));

            StringAssert.Contains(ex.Message, "INVALID_ACCOUNT");
        }
    }
}
