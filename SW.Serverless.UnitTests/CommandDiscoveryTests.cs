using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Resident;
using SW.Serverless.UnitTests.Fixtures;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// What an adapter can be asked to do, discovered rather than documented.
    ///
    /// An adapter is a zip in cloud storage written by somebody else, possibly a long time ago. The
    /// host has always known the NAMES of its commands; what it could not answer is whether a
    /// command takes an argument, what that argument looks like, or whether anything comes back —
    /// so anything wanting to offer those commands had to hard-code them, which is the same as not
    /// discovering them at all.
    /// </summary>
    [TestClass]
    public class CommandDiscoveryTests
    {
        const string GreedyId = "test.discovery";

        static IHost host;
        static IResidentAdapterHost adapters;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            host = Host.CreateDefaultBuilder()
                .ConfigureLogging(l => l.ClearProviders())
                .ConfigureServices(s =>
                {
                    s.AddLocalTestsCloudFiles(o => o.BucketName = TestStore.BucketName + "-discovery");
                    s.AddServerless(o =>
                    {
                        o.AdapterRemotePath = "adapters";
                        o.AdapterLocalPath = Path.Combine(Path.GetTempPath(), "swsl-disctests", "installed");
                        o.AdapterMetadataCacheDuration = 1;
                    });
                    s.AddSingleton<TestEventSink>();
                    s.AddSingleton<IAdapterEventSink>(sp => sp.GetRequiredService<TestEventSink>());
                    s.AddResidentAdapters<TestEventSink>(o =>
                    {
                        o.SocketPath = $"/tmp/swsl-d{Environment.ProcessId}.sock";
                        o.PipeName = $"swsl-d{Environment.ProcessId}";
                        o.HeartbeatInterval = TimeSpan.FromSeconds(2);
                        o.HandshakeTimeout = TimeSpan.FromSeconds(30);
                    });
                })
                .Build();

            await host.StartAsync();
            adapters = host.Services.GetRequiredService<IResidentAdapterHost>();

            await TestStore.PublishAsync(host.Services.GetRequiredService<ICloudFilesService>(),
                GreedyId, "SW.Serverless.Samples.Greedy",
                new Dictionary<string, string> { ["Protocol"] = "2", ["Lifecycle"] = "resident" });

            await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = GreedyId,
                InstanceKey = "discovery",
            });
        }

        [ClassCleanup]
        public static async Task ClassCleanup()
        {
            if (host != null) await host.StopAsync();
            host?.Dispose();
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "swsl-disctests"), true); } catch { }
        }

        static IReadOnlyCollection<AdapterCommand> Commands() =>
            adapters.Describe().Single(h => h.InstanceKey == "discovery").CommandDetails;

        [TestMethod]
        public void Every_public_command_is_discovered_without_being_declared_anywhere()
        {
            var names = Commands().Select(c => c.Name).ToList();

            CollectionAssert.IsSubsetOf(
                new[] { "Allocate", "Release", "BurnCpu", "GetStats", "Strain" },
                names.ToList());

            // The lifecycle methods are not commands. Offering StopAsync as something to click
            // would hand an operator a way to stop an adapter that looks like any other action.
            CollectionAssert.DoesNotContain(names, "StartAsync");
            CollectionAssert.DoesNotContain(names, "StopAsync");
            CollectionAssert.DoesNotContain(names, "GetStatusAsync");
        }

        /// <summary>
        /// Whether a command takes an argument, and of what type. Without this a caller can only
        /// guess, and guessing wrong on a command that writes to a customer's system is not a
        /// mistake anyone wants to make twice.
        /// </summary>
        [TestMethod]
        public void A_commands_argument_is_described()
        {
            var allocate = Commands().Single(c => c.Name == "Allocate");
            Assert.IsTrue(allocate.TakesArgument);
            Assert.AreEqual("Int32", allocate.ParameterType);
            Assert.IsTrue(allocate.ReturnsValue);

            var release = Commands().Single(c => c.Name == "Release");
            Assert.IsFalse(release.TakesArgument, "Release takes nothing and should say so");
            Assert.AreEqual("", release.ParameterType);
        }

        /// <summary>
        /// A shaped argument comes with its properties, which is what lets a UI build a form for a
        /// command it has never seen — the difference between discovery and a list of names.
        /// </summary>
        [TestMethod]
        public void A_shaped_argument_carries_its_properties()
        {
            var strain = Commands().Single(c => c.Name == "Strain");

            Assert.AreEqual("StrainRequest", strain.ParameterType);
            Assert.IsFalse(string.IsNullOrEmpty(strain.ParameterSchema),
                "a complex argument should describe its shape");

            var schema = JObject.Parse(strain.ParameterSchema);
            Assert.AreEqual("Int32", schema.Value<string>("AllocateMb"));
            Assert.AreEqual("Double", schema.Value<string>("BurnSeconds"));
            Assert.AreEqual("String", schema.Value<string>("Note"));
        }

        /// <summary>
        /// A primitive argument gets no schema. An object with one entry called "value" would be
        /// noise pretending to be information — ParameterType already said everything.
        /// </summary>
        [TestMethod]
        public void A_primitive_argument_gets_no_schema()
        {
            var allocate = Commands().Single(c => c.Name == "Allocate");
            Assert.AreEqual("", allocate.ParameterSchema);
        }

        [TestMethod]
        public void A_description_reaches_the_host_when_the_author_wrote_one()
        {
            var allocate = Commands().Single(c => c.Name == "Allocate");
            StringAssert.Contains(allocate.Description, "megabytes");

            // And an undescribed command is still discovered — the attribute is optional.
            var stats = Commands().Single(c => c.Name == "GetStats");
            Assert.IsNotNull(stats);
            Assert.AreEqual("", stats.Description);
        }

        /// <summary>
        /// The names list stays exactly as it was. An adapter built against an older SDK sends no
        /// descriptors at all, and everything reading the old field has to keep working — so the
        /// two are kept in step rather than one replacing the other.
        /// </summary>
        [TestMethod]
        public void The_names_and_the_descriptors_agree()
        {
            var health = adapters.Describe().Single(h => h.InstanceKey == "discovery");

            CollectionAssert.AreEquivalent(
                health.Commands.OrderBy(c => c).ToList(),
                health.CommandDetails.Select(c => c.Name).OrderBy(c => c).ToList());
        }

        /// <summary>
        /// Discovery is only worth anything if what it describes can then be called. This takes a
        /// command's declared shape and invokes it from that alone.
        /// </summary>
        [TestMethod]
        public async Task A_discovered_command_can_be_called_from_its_description_alone()
        {
            var strain = Commands().Single(c => c.Name == "Strain");
            var schema = JObject.Parse(strain.ParameterSchema);

            // Built from the schema, not from knowledge of StrainRequest.
            var argument = new JObject();
            foreach (var property in schema.Properties())
                argument[property.Name] = property.Value.ToString() switch
                {
                    "Int32" => 7,
                    "Double" => 0d,
                    _ => (JToken)"from discovery",
                };

            var result = await adapters.Get(GreedyId, "discovery")
                .InvokeAsync<JObject>(strain.Name, argument, timeoutSeconds: 30);

            Assert.IsNotNull(result);
            Assert.AreEqual(7, result.Value<int>("allocatedMb"));
        }
    }
}
