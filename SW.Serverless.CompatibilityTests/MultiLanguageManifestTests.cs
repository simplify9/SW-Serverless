using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Contract.Catalog;

namespace SW.Serverless.CompatibilityTests;

/// <summary>
/// The manifest fields multi-language adapters add — runtime version, platforms and their entries,
/// contracts, the source the package carries — written by the current tools and read by the
/// first published manifest parser (SimplyWorks.Serverless.Contract 10.0.2), which deployed applications use. That parser
/// must keep every one of them, find nothing wrong, and still see the fields it knows as before.
/// </summary>
[TestClass]
public class MultiLanguageManifestTests
{
    static AdapterManifest Written() => new()
    {
        Id = "acme.orders",
        Version = "2.1.0",
        DisplayName = "Acme orders",
        Kinds = { "handler" },
        Runtime = AdapterManifest.PythonRuntime,
        RuntimeVersion = ">=3.12",
        Language = "python",
        Entry = "main.py",
        Lifecycle = AdapterManifest.ClassicLifecycle,
        Platforms = new() { "linux-x64", "linux-arm64" },
        Entries = new() { ["linux-x64"] = "x64/main.py", ["linux-arm64"] = "arm64/main.py" },
        Contracts = new() { ["orders"] = 1 },
        Source = new AdapterSource
        {
            Files = new() { ["main.py"] = new string('a', 64), ["requirements.lock"] = new string('b', 64) },
            BuildCommand = "serverless build",
            Lockfiles = new() { "requirements.lock" },
        },
    };

    [TestMethod]
    public async Task The_released_manifest_parser_keeps_every_new_field_and_finds_nothing_wrong()
    {
        var written = Written();
        Assert.AreEqual(0, written.Validate().Count, string.Join("; ", written.Validate()));
        var path = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, written.ToJson());
        try
        {
            var read = await Compat.ReadWithManifestV1002Async(path);

            Assert.AreEqual(0, read.ExitCode, read.ToString());
            Assert.IsNull(read.Value("PROBLEM"), read.ToString());
            Assert.AreEqual("main.py", read.Value("ENTRY"), "the default entry is what released readers use");
            Assert.AreEqual("python", read.Value("RUNTIME"), "a released host sees the runtime, and refuses one it can't run");

            var before = JsonNode.Parse(written.ToJson())!;
            var after = JsonNode.Parse(read.Value("JSON")!)!;
            foreach (var field in new[] { "runtimeVersion", "platforms", "entries", "contracts", "source" })
                Assert.IsTrue(JsonNode.DeepEquals(before[field], after[field]), $"{field} changed in a round trip through 10.0.2:\n{after[field]}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void The_new_fields_round_trip_through_the_current_parser()
    {
        var again = AdapterManifest.Parse(Written().ToJson());

        Assert.AreEqual(">=3.12", again.RuntimeVersion);
        CollectionAssert.AreEqual(new[] { "linux-x64", "linux-arm64" }, again.Platforms);
        Assert.AreEqual("arm64/main.py", again.EntryFor("linux-arm64"));
        Assert.AreEqual("main.py", again.EntryFor("osx-arm64"), "a platform without its own entry uses the default");
        Assert.AreEqual(1, again.Contracts!["orders"]);
        Assert.AreEqual(AdapterSource.DefaultPath, again.Source!.Path);
        Assert.AreEqual(2, again.Source.Files.Count);
        Assert.IsNull(again.Extensions, "nothing unknown to the current parser");
    }

    [TestMethod]
    public void A_manifest_without_the_new_fields_is_read_as_a_dotnet_adapter_as_before()
    {
        var old = AdapterManifest.Parse("""{ "id": "legacy", "entry": "Legacy.dll" }""");

        Assert.AreEqual(AdapterManifest.DotnetRuntime, old.Runtime);
        Assert.AreEqual("Legacy.dll", old.EntryFor("linux-x64"));
        Assert.IsNull(old.Platforms);
        Assert.IsNull(old.Source);
        Assert.AreEqual(0, old.Validate().Count);
    }

    [DataTestMethod]
    [DataRow("""{ "runtime": "../bin/sh" }""", "runtime")]
    [DataRow("""{ "platforms": ["Linux x64"] }""", "platform")]
    [DataRow("""{ "platforms": ["linux-x64"], "entries": { "osx-arm64": "a" } }""", "doesn't list")]
    [DataRow("""{ "platforms": ["linux-x64"], "entries": { "linux-x64": "../../etc/passwd" } }""", "inside the package")]
    [DataRow("""{ "contracts": { "orders": 0 } }""", "contract")]
    [DataRow("""{ "source": { "files": { "main.py": "not-a-hash" } } }""", "SHA-256")]
    [DataRow("""{ "source": { "path": "/abs", "files": {} } }""", "source.path")]
    public void A_wrong_new_field_is_named_by_validation(string json, string expected)
    {
        var problems = AdapterManifest.Parse(json).Validate();
        Assert.IsTrue(problems.Any(p => p.Contains(expected)), $"expected a problem about {expected}: {string.Join("; ", problems)}");
    }
}
