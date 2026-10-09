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
/// The conformance kit — what sw-serverless test runs — against real adapters built beside the
/// tests: started as a host starts them, described, and called with the examples of the sample
/// "orders" contract in Contracts/, handed to the kit as any application's contract would be.
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

    static string ContractFile => Path.Combine(AppContext.BaseDirectory, "Contracts", "orders-adapter-contract.v1.json");

    static AdapterManifest ProcessorManifest(bool listApiKey = true, string contract = "orders")
    {
        var manifest = new AdapterManifest
        {
            Id = "test.orders.processor",
            Kinds = { "processor" },
            Contracts = new() { [contract] = 1 },
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
        bool allowDelete = false, bool withContract = true)
    {
        var report = await new ConformanceRunner().RunAsync(new ConformanceOptions
        {
            PackageDirectory = packageDirectory,
            Settings = settings ?? new Dictionary<string, string> { ["ApiKey"] = "test-key" },
            AllowDelete = allowDelete,
            CommandTimeoutSeconds = 30,
            Contracts = withContract ? new List<ContractDocument> { ContractDocument.FromFile(ContractFile) } : new List<ContractDocument>(),
        });
        Console.WriteLine(string.Join(Environment.NewLine, report.Checks.Select(c => $"{c.Outcome,-7} {c.Name} {c.Detail}")));
        return report;
    }

    static ConformanceCheck Check(ConformanceReport report, string name) =>
        report.Checks.Single(c => c.Name == name);

    [TestMethod]
    public async Task A_conforming_processor_passes_every_check()
    {
        var report = await RunAsync(Package("SW.Serverless.UnitTests.OrdersProcessor", ProcessorManifest()));

        Assert.IsTrue(report.Passed, string.Join("; ", report.Checks.Where(c => c.Outcome == CheckOutcome.Failed)));
        foreach (var name in new[]
                 {
                     "manifest", "describe", "settings match the manifest", "starts",
                     "orders processor: methods", "orders processor: Process answers example 1", "an unknown command is refused",
                 })
            Assert.AreEqual(CheckOutcome.Passed, Check(report, name).Outcome, name);
    }

    [TestMethod]
    public async Task A_processor_answering_without_what_the_schema_requires_fails_the_contract()
    {
        var report = await RunAsync(Package("SW.Serverless.UnitTests.OrdersProcessor", ProcessorManifest()),
            new Dictionary<string, string> { ["ApiKey"] = "k", ["Mode"] = "broken" });

        var check = Check(report, "orders processor: Process answers example 1");
        Assert.AreEqual(CheckOutcome.Failed, check.Outcome);
        StringAssert.Contains(check.Detail, "isn't a valid Receipt");
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public async Task Settings_the_manifest_leaves_out_are_named()
    {
        var report = await RunAsync(Package("SW.Serverless.UnitTests.OrdersProcessor", ProcessorManifest(listApiKey: false)));

        var check = Check(report, "settings match the manifest");
        Assert.AreEqual(CheckOutcome.Failed, check.Outcome);
        StringAssert.Contains(check.Detail, "'ApiKey' is declared by the adapter but missing from the manifest");
    }

    static (string Package, string Folder) Source()
    {
        var folder = Path.Combine(Path.GetTempPath(), "swsl-conformance-tests", "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.json"), "{\"n\":1}");
        File.WriteAllText(Path.Combine(folder, "b.json"), "{\"n\":2}");

        var package = Package("SW.Serverless.UnitTests.OrdersSource", new AdapterManifest
        {
            Id = "test.orders.source",
            Kinds = { "source" },
            Contracts = new() { ["orders"] = 1 },
            // Run classically, one session at a time, over gRPC.
            Protocol = new AdapterProtocolRange { Min = 2, Max = 2 },
            Properties = { new AdapterProperty { Name = "Folder", Required = true } },
        });
        return (package, folder);
    }

    [TestMethod]
    public async Task A_session_kind_runs_in_order_and_leaves_the_source_alone_unless_allowed()
    {
        var (package, folder) = Source();
        var report = await RunAsync(package, new Dictionary<string, string> { ["Folder"] = folder });

        Assert.IsTrue(report.Passed, string.Join("; ", report.Checks.Where(c => c.Outcome == CheckOutcome.Failed)));
        Assert.AreEqual(CheckOutcome.Passed, Check(report, "orders source: List").Outcome);
        Assert.AreEqual(CheckOutcome.Passed, Check(report, "orders source: Fetch").Outcome);
        Assert.AreEqual(CheckOutcome.Skipped, Check(report, "orders source: Remove").Outcome);
        Assert.AreEqual(2, Directory.GetFiles(folder).Length, "nothing was deleted");
    }

    [TestMethod]
    public async Task A_destructive_method_runs_when_allowed()
    {
        var (package, folder) = Source();
        var report = await RunAsync(package, new Dictionary<string, string> { ["Folder"] = folder }, allowDelete: true);

        Assert.AreEqual(CheckOutcome.Passed, Check(report, "orders source: Remove").Outcome);
        CollectionAssert.AreEqual(new[] { "b.json" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray(),
            "the first file listed was deleted");
    }

    [TestMethod]
    public async Task A_contract_the_kit_isn_t_given_is_named_with_how_to_give_it()
    {
        var report = await RunAsync(Package("SW.Serverless.UnitTests.OrdersProcessor", ProcessorManifest(contract: "unheard-of")),
            withContract: false);

        var check = Check(report, "contract unheard-of v1");
        Assert.AreEqual(CheckOutcome.Failed, check.Outcome);
        StringAssert.Contains(check.Detail, "--contract");
    }

    [TestMethod]
    public async Task A_registered_contract_is_checked_without_being_handed_over()
    {
        // What an application's own CLI does with its contract, once, at start.
        ContractDocument.Register(ContractDocument.FromJson(File.ReadAllText(ContractFile),
            file => File.ReadAllText(Path.Combine(Path.GetDirectoryName(ContractFile)!, file))));

        var report = await RunAsync(Package("SW.Serverless.UnitTests.OrdersProcessor", ProcessorManifest()), withContract: false);

        Assert.AreEqual(CheckOutcome.Passed, Check(report, "orders processor: Process answers example 1").Outcome);
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
