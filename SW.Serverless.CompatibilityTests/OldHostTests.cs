using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Installer;

namespace SW.Serverless.CompatibilityTests;

/// <summary>
/// (a) A host on the PUBLISHED SimplyWorks.Serverless 10.0.0 — what ten production deployments run —
/// installs and runs adapters published by the current installer: versioned, with a manifest in the
/// package and a catalog beside it. Old hosts only ever read <c>adapters/{id}</c> and its metadata,
/// so that is what has to stay right through publish, promote and rollback.
/// </summary>
[TestClass]
public class OldHostTests
{
    const string Sample = "SW.Serverless.Samples.Classic";
    const string Id = "compat.classic";

    static Bucket bucket = null!;

    /// <summary>
    /// Published through the command line, the way CI does it — a real <c>dotnet publish</c> of the
    /// sample, not a copy of its build output.
    /// </summary>
    [ClassInitialize]
    public static async Task Publish(TestContext _)
    {
        bucket = new Bucket();
        var project = Compat.ProjectFile(Sample);

        Assert.AreEqual(Program.Success,
            await Program.RunAsync(new[] { project, Id, "-v", "1.0.0" }.Concat(bucket.Flags).ToArray(), _ => null),
            "the first versioned publish failed");
        Assert.AreEqual(Program.Success,
            await Program.RunAsync(new[] { project, Id, "-v", "minor", "--notes", "Second" }.Concat(bucket.Flags).ToArray(), _ => null),
            "the second versioned publish failed");
    }

    [ClassCleanup]
    public static void Cleanup() => bucket?.Dispose();

    static async Task<JObject> MetadataSeenByOldHost(string adapter)
    {
        var run = await Compat.RunOldHostAsync(bucket, adapter, "--metadata", "yes");
        Assert.AreEqual(0, run.ExitCode, run.ToString());
        return JObject.Parse(run.Value("METADATA")!);
    }

    [TestMethod]
    public async Task An_old_host_runs_the_current_version_and_follows_promote_and_rollback()
    {
        // Published 1.0.0 then 1.1.0, which is current.
        var run = await Compat.RunOldHostAsync(bucket, Id, "--command", "Echo", "--input", "hello", "--expected", "yes");
        Assert.AreEqual(0, run.ExitCode, run.ToString());
        Assert.AreEqual("hello", run.Value("RESULT"), run.ToString());

        var expected = JObject.Parse(run.Value("EXPECTED")!);
        Assert.IsTrue((bool)expected["ApiKey"]!["Private"]!, "expected startup values are answered as before");
        Assert.AreEqual("https://api.example.test", (string?)expected["BaseUrl"]!["Default"]);

        Assert.AreEqual("1.1.0", (string?)(await MetadataSeenByOldHost(Id))["Version"]);

        // Roll back with the command line, then check the old host now gets 1.0.0's package.
        Assert.AreEqual(Program.Success,
            await Program.RunAsync(new[] { "promote", Id, "1.0.0" }.Concat(bucket.Flags).ToArray(), _ => null));

        var metadata = await MetadataSeenByOldHost(Id);
        Assert.AreEqual("1.0.0", (string?)metadata["Version"]);
        var entry = await new AdapterCatalogStore(bucket.Files).GetAsync(Id);
        Assert.AreEqual(entry.Find("1.0.0").Sha256, (string?)metadata["Sha256"]);
        Assert.AreEqual(entry.Find("1.0.0").Sha256, (string?)metadata["Hash"],
            "the filesystem provider adds no Hash, so the installer must write it or no host can install");

        run = await Compat.RunOldHostAsync(bucket, Id, "--command", "Echo", "--input", "after rollback");
        Assert.AreEqual(0, run.ExitCode, run.ToString());
        Assert.AreEqual("after rollback", run.Value("RESULT"), run.ToString());

        // And forward again.
        Assert.AreEqual(Program.Success,
            await Program.RunAsync(new[] { "promote", Id, "1.1.0" }.Concat(bucket.Flags).ToArray(), _ => null));
        Assert.AreEqual("1.1.0", (string?)(await MetadataSeenByOldHost(Id))["Version"]);
    }

    [TestMethod]
    public async Task The_package_an_old_host_installs_carries_the_manifest()
    {
        var metadata = await MetadataSeenByOldHost(Id);
        foreach (var key in new[] { "EntryAssembly", "Lang", "Timestamp", "Lifecycle", "Kind", "Sha256", "Hash", "Version" })
            Assert.IsNotNull(metadata[key], $"the old host did not see {key}");
        Assert.AreEqual(Sample + ".dll", (string?)metadata["EntryAssembly"]);
        Assert.AreEqual("classic", (string?)metadata["Lifecycle"]);

        var entry = await new AdapterCatalogStore(bucket.Files).GetAsync(Id);
        Assert.IsNotNull(entry.Manifest);
        Assert.AreEqual(3, entry.Manifest.Properties.Count, "the CLI probes a classic adapter by default");
        Assert.AreEqual("Second", entry.Find("1.1.0").Manifest.ReleaseNotes);
    }

    /// <summary>An unversioned publish — today's command line, unchanged — still runs on an old host.</summary>
    [TestMethod]
    public async Task An_old_host_runs_an_unversioned_publish()
    {
        await Compat.PublishAsync(bucket, Sample, "compat.unversioned", null);

        var run = await Compat.RunOldHostAsync(bucket, "compat.unversioned", "--command", "Echo", "--input", "plain");
        Assert.AreEqual(0, run.ExitCode, run.ToString());
        Assert.AreEqual("plain", run.Value("RESULT"));
    }

    /// <summary>
    /// Pinning on an old host: it reads a pinned ref at <c>adapters/{id}/{version}</c>, which is
    /// where an installer before this one put versions — and those keep working, because nothing
    /// here moves or rewrites them.
    /// </summary>
    [TestMethod]
    public async Task An_old_host_still_runs_a_version_pinned_where_an_older_installer_put_it()
    {
        await Compat.PublishTheOldWayAsync(bucket.Files, "adapters/compat.pinned/2.0.0", "SW.Serverless.Compat.ClassicV10");

        var run = await Compat.RunOldHostAsync(bucket, "compat.pinned/2.0.0", "--command", "Echo", "--input", "pinned");
        Assert.AreEqual(0, run.ExitCode, run.ToString());
        Assert.AreEqual("pinned", run.Value("RESULT"));

        // The current host reads the same key through its fallback.
        using var host = Compat.NewHost(bucket);
        using var service = (ServerlessService)host.GetRequiredService<SW.PrimitiveTypes.IServerlessService>();
        await service.StartAsync("compat.pinned/2.0.0", "c");
        Assert.AreEqual("pinned", await service.InvokeAsync<string>("Echo", "pinned"));
    }

    /// <summary>
    /// The one thing an old host cannot do: pin a version in the new layout. Versions now live under
    /// <c>adapters-versions/</c>, which an old host does not know, so it reports the adapter as
    /// missing — it does not run something else. Pinning was never used by an old host (nothing
    /// passed a pinned ref), and the current host resolves it; this records the boundary.
    /// </summary>
    [TestMethod]
    public async Task An_old_host_cannot_pin_a_version_in_the_new_layout_but_the_current_host_can()
    {
        var run = await Compat.RunOldHostAsync(bucket, $"{Id}/1.0.0", "--command", "Echo", "--input", "x");
        Assert.AreNotEqual(0, run.ExitCode, run.ToString());
        Assert.IsNull(run.Value("RESULT"));

        using var host = Compat.NewHost(bucket);
        using var service = (ServerlessService)host.GetRequiredService<SW.PrimitiveTypes.IServerlessService>();
        await service.StartAsync($"{Id}/1.0.0", "c");
        Assert.AreEqual("pinned on the new host", await service.InvokeAsync<string>("Echo", "pinned on the new host"));
    }
}
