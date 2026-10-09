using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Tooling.Conformance;

namespace SW.Serverless.Installer.UnitTests;

/// <summary>
/// The conformance kit — what serverless test runs — against real adapters built beside the tests:
/// started as a host starts them, described, and called with the Bitween contract's examples.
/// </summary>
[TestClass]
public class ConformanceTests
{
    static string BuildOutput(string project)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SW.Serverless.sln"))) dir = dir.Parent;
        var bin = Path.Combine(dir!.FullName, project, "bin");
        return Directory.GetDirectories(bin, "*", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, project + ".dll")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, project + ".dll")))
            .First();
    }

    /// <summary>A package: the project's build output with this manifest at its root.</summary>
    static string Package(string project, AdapterManifest manifest)
    {
        var target = Path.Combine(Path.GetTempPath(), "swsl-conformance-tests", Guid.NewGuid().ToString("N"));
        var source = BuildOutput(project);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        manifest.Entry ??= project + ".dll";
        File.WriteAllText(Path.Combine(target, AdapterManifest.FileName), manifest.ToJson());
        return target;
    }

    static AdapterManifest HandlerManifest(bool listApiKey = true)
    {
        var manifest = new AdapterManifest
        {
            Id = "test.bitween.handler",
            Kinds = { "handler" },
            Contracts = new() { ["bitween"] = 1 },
            Properties =
            {
                new AdapterProperty { Name = "Endpoint", Default = "https://partner.example.test/orders", Description = "Where orders go." },
                new AdapterProperty { Name = "Mode", Default = "working" },
            },
        };
        if (listApiKey) manifest.Properties.Add(new AdapterProperty { Name = "ApiKey", Required = true, Secret = true });
        return manifest;
    }

    static async Task<ConformanceReport> RunAsync(string packageDirectory, IDictionary<string, string> settings = null,
        bool allowDelete = false)
    {
        var report = await new ConformanceRunner().RunAsync(new ConformanceOptions
        {
            PackageDirectory = packageDirectory,
            Settings = settings ?? new Dictionary<string, string> { ["ApiKey"] = "test-key" },
            AllowDelete = allowDelete,
            CommandTimeoutSeconds = 30,
        });
        Console.WriteLine(string.Join(Environment.NewLine, report.Checks.Select(c => $"{c.Outcome,-7} {c.Name} {c.Detail}")));
        return report;
    }

    static ConformanceCheck Check(ConformanceReport report, string name) =>
        report.Checks.Single(c => c.Name == name);

    [TestMethod]
    public async Task A_conforming_handler_passes_every_check()
    {
        var report = await RunAsync(Package("SW.Serverless.UnitTests.BitweenHandler", HandlerManifest()));

        Assert.IsTrue(report.Passed, string.Join("; ", report.Checks.Where(c => c.Outcome == CheckOutcome.Failed)));
        foreach (var name in new[]
                 {
                     "manifest", "describe", "settings match the manifest", "starts",
                     "bitween handler: methods", "bitween handler: Handle answers example 1", "an unknown command is refused",
                 })
            Assert.AreEqual(CheckOutcome.Passed, Check(report, name).Outcome, name);
    }

    [TestMethod]
    public async Task A_handler_answering_without_data_fails_the_contract()
    {
        var report = await RunAsync(Package("SW.Serverless.UnitTests.BitweenHandler", HandlerManifest()),
            new Dictionary<string, string> { ["ApiKey"] = "k", ["Mode"] = "broken" });

        var check = Check(report, "bitween handler: Handle answers example 1");
        Assert.AreEqual(CheckOutcome.Failed, check.Outcome);
        StringAssert.Contains(check.Detail, "isn't a valid ExchangeFile");
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public async Task Settings_the_manifest_leaves_out_are_named()
    {
        var report = await RunAsync(Package("SW.Serverless.UnitTests.BitweenHandler", HandlerManifest(listApiKey: false)));

        var check = Check(report, "settings match the manifest");
        Assert.AreEqual(CheckOutcome.Failed, check.Outcome);
        StringAssert.Contains(check.Detail, "'ApiKey' is declared by the adapter but missing from the manifest");
    }

    static (string Package, string Folder) Receiver()
    {
        var folder = Path.Combine(Path.GetTempPath(), "swsl-conformance-tests", "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.json"), "{\"n\":1}");
        File.WriteAllText(Path.Combine(folder, "b.json"), "{\"n\":2}");

        var package = Package("SW.Serverless.UnitTests.BitweenReceiver", new AdapterManifest
        {
            Id = "test.bitween.receiver",
            Kinds = { "receiver" },
            Contracts = new() { ["bitween"] = 1 },
            // Run as Bitween runs receivers, classically, over gRPC.
            Protocol = new AdapterProtocolRange { Min = 2, Max = 2 },
            Properties = { new AdapterProperty { Name = "Folder", Required = true } },
        });
        return (package, folder);
    }

    [TestMethod]
    public async Task A_receiver_runs_its_session_in_order_and_leaves_the_source_alone_unless_allowed()
    {
        var (package, folder) = Receiver();
        var report = await RunAsync(package, new Dictionary<string, string> { ["Folder"] = folder });

        Assert.IsTrue(report.Passed, string.Join("; ", report.Checks.Where(c => c.Outcome == CheckOutcome.Failed)));
        Assert.AreEqual(CheckOutcome.Passed, Check(report, "bitween receiver: ListFiles").Outcome);
        Assert.AreEqual(CheckOutcome.Passed, Check(report, "bitween receiver: GetFile").Outcome);
        Assert.AreEqual(CheckOutcome.Skipped, Check(report, "bitween receiver: DeleteFile").Outcome);
        Assert.AreEqual(2, Directory.GetFiles(folder).Length, "nothing was deleted");
    }

    [TestMethod]
    public async Task A_receiver_s_delete_runs_when_allowed()
    {
        var (package, folder) = Receiver();
        var report = await RunAsync(package, new Dictionary<string, string> { ["Folder"] = folder }, allowDelete: true);

        Assert.AreEqual(CheckOutcome.Passed, Check(report, "bitween receiver: DeleteFile").Outcome);
        CollectionAssert.AreEqual(new[] { "b.json" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray(),
            "the first file listed was deleted");
    }

    [TestMethod]
    public async Task An_adapter_on_the_old_SDK_is_told_it_can_t_describe_itself()
    {
        var report = await RunAsync(Package("SW.Serverless.Compat.ClassicV10", new AdapterManifest { Id = "test.old" }));

        var describe = Check(report, "describe");
        Assert.AreEqual(CheckOutcome.Failed, describe.Outcome);
        StringAssert.Contains(describe.Detail, "older than 10.1.0");
        Assert.AreEqual(CheckOutcome.Passed, Check(report, "starts").Outcome, "it still runs");
    }
}
