using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.UnitTests.Fixtures;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// An adapter started with --describe prints what it is — settings, commands with JSON Schemas,
    /// kinds, contracts — and exits, without any host. This is how the tools learn about an adapter
    /// in any language, rather than by reading .NET assemblies.
    /// </summary>
    [TestClass]
    public class DescribeTests
    {
        static AdapterSelfDescription Describe(string project)
        {
            var entry = Path.Combine(TestStore.LocateBuildOutput(project), project + ".dll");
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { entry, AdapterSelfDescription.Flag },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            })!;
            // Nothing is ever written to it: a describe never waits on stdin.
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            Assert.IsTrue(process.WaitForExit(30_000), "--describe did not exit");
            Assert.AreEqual(0, process.ExitCode, error);
            return AdapterSelfDescription.Parse(output);
        }

        [TestMethod]
        public void A_classic_adapter_describes_its_settings_commands_and_kinds()
        {
            var d = Describe("SW.Serverless.Samples.Classic");

            Assert.AreEqual("dotnet", d.SdkLanguage);
            Assert.AreEqual("10.1.0", d.SdkVersion);
            Assert.AreEqual(AdapterManifest.ClassicLifecycle, d.Lifecycle);
            Assert.AreEqual(1, d.Protocol.Max, "the classic text protocol");
            CollectionAssert.AreEquivalent(new[] { "handler", "mapper" }, d.Kinds);

            var apiKey = d.Settings.Single(s => s.Name == "ApiKey");
            Assert.IsTrue(apiKey.Required);
            Assert.IsTrue(apiKey.Secret);
            Assert.IsNull(apiKey.Default, "a secret's default is never published");
            Assert.AreEqual("https://api.example.test", d.Settings.Single(s => s.Name == "BaseUrl").Default);

            var echo = d.Commands.Single(c => c.Name == "Echo");
            StringAssert.Contains(echo.InputSchema!.Value.GetRawText(), "string");
            Assert.IsTrue(echo.ReturnsValue);
            Assert.IsFalse(d.Commands.Single(c => c.Name == "Fail").ReturnsValue);

            // An object argument is described by its properties, named as they travel.
            var summarize = d.Commands.Single(c => c.Name == "Summarize").InputSchema!.Value;
            Assert.IsTrue(summarize.GetProperty("properties").TryGetProperty("Lines", out _));
        }

        [TestMethod]
        public void A_resident_adapter_describes_its_commands_and_leaves_out_its_lifecycle_methods()
        {
            var d = Describe("SW.Serverless.Samples.Greedy");

            Assert.AreEqual(AdapterManifest.ResidentLifecycle, d.Lifecycle);
            Assert.AreEqual(2, d.Protocol.Min, "gRPC");
            var names = d.Commands.Select(c => c.Name).ToList();
            CollectionAssert.IsSubsetOf(new[] { "Allocate", "GetStats", "Release" }, names);
            CollectionAssert.DoesNotContain(names, "StartAsync");
            CollectionAssert.DoesNotContain(names, "StopAsync");
        }

        [TestMethod]
        public void An_adapter_describes_the_contracts_it_implements()
        {
            var d = Describe("SW.Serverless.UnitTests.GrpcClassicAdapter");

            Assert.AreEqual(1, d.Contracts["orders"]);
            CollectionAssert.AreEqual(new[] { "processor" }, d.Kinds);
            Assert.AreEqual("hello ", d.Settings.Single(s => s.Name == "Prefix").Default);
            Assert.AreEqual(0, d.Warnings.Count, string.Join("; ", d.Warnings));
        }

        [TestMethod]
        public void The_description_round_trips_through_its_own_parser()
        {
            var d = Describe("SW.Serverless.Samples.Classic");
            var again = AdapterSelfDescription.Parse(d.ToJson());

            Assert.AreEqual(d.Commands.Count, again.Commands.Count);
            Assert.AreEqual(d.Settings.Count, again.Settings.Count);
            Assert.IsTrue(JsonElement.DeepEquals(
                d.Commands.Single(c => c.Name == "Summarize").InputSchema!.Value,
                again.Commands.Single(c => c.Name == "Summarize").InputSchema!.Value));
        }
    }
}
