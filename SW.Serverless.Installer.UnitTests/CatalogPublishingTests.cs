using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.PrimitiveTypes;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Tooling;

namespace SW.Serverless.Installer.UnitTests;

/// <summary>
/// Publishing with a manifest and a catalog, end to end against storage — the filesystem provider,
/// as a developer would publish, or <see cref="ObjectStore"/> where the test needs the flat keys an
/// older installer left in an object store.
///
/// The publish runs over the REAL build output of the sample adapters rather than a fixture, so
/// what is described, probed and packaged is what an author's project would produce.
/// </summary>
[TestClass]
public class CatalogPublishingTests
{
    const string Classic = "SW.Serverless.Samples.Classic";
    const string Resident = "SW.Serverless.Samples.Greedy";

    string root = null!;
    ICloudFilesService store = null!;
    readonly List<string> output = new();

    [TestInitialize]
    public void Init()
    {
        root = Path.Combine(Path.GetTempPath(), "swsl-installer-catalog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        store = CloudFilesFactory.Create(LocalOptions());
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    ServerlessUploadOptions LocalOptions() => new()
    {
        Provider = "local",
        BucketName = "catalog-tests",
        ServiceUrl = Path.Combine(root, "store"),
    };

    string[] LocalFlags() => new[] { "-p", "local", "-b", "catalog-tests", "-u", Path.Combine(root, "store") };

    // ---------------------------------------------------------------- fixtures

    internal static string BuildOutput(string project)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, project))) dir = dir.Parent;
        Assert.IsNotNull(dir, $"could not find the {project} project");

        var bin = Path.Combine(dir!.FullName, project, "bin");
        var found = Directory.Exists(bin)
            ? Directory.EnumerateDirectories(bin, "net*", SearchOption.AllDirectories)
                .Where(d => File.Exists(Path.Combine(d, project + ".dll")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, project + ".dll")))
                .FirstOrDefault()
            : null;
        Assert.IsNotNull(found, $"{project} was not built beside the tests");
        return found!;
    }

    /// <summary>A fresh copy of the sample's build output — the publisher writes adapter.json into it.</summary>
    string PublishDirectory(string project)
    {
        var target = Path.Combine(root, "publish-" + Guid.NewGuid().ToString("N"));
        var source = BuildOutput(project);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return target;
    }

    /// <summary>A project directory holding only what the publisher reads from it: the author's adapter.json.</summary>
    string Project(string? authorJson = null)
    {
        var dir = Path.Combine(root, "project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var project = Path.Combine(dir, "Adapter.csproj");
        File.WriteAllText(project, "<Project />");
        if (authorJson != null) File.WriteAllText(Path.Combine(dir, AdapterManifest.FileName), authorJson);
        return project;
    }

    Task<PublishResult> Publish(string id, string? version, bool promote = true, string? authorJson = null,
        bool probe = false, string project = Classic, string? publishDirectory = null, string? kind = null,
        string? notes = null, ICloudFilesService? files = null)
    {
        publishDirectory ??= PublishDirectory(project);
        return PackagePublisher.PublishAsync(files ?? store, new PublishRequest
        {
            AdapterId = id,
            ProjectPath = Project(authorJson),
            PublishPath = publishDirectory,
            EntryAssembly = project + ".dll",
            WorkPath = Path.Combine(root, "work-" + Guid.NewGuid().ToString("N")),
            Version = version,
            Promote = promote,
            Probe = probe,
            Kind = kind,
            ReleaseNotes = notes,
            PublishedBy = "tester",
        }, output.Add);
    }

    async Task<List<string>> Keys(string prefix = "", ICloudFilesService? files = null) =>
        (await (files ?? store).ListAsync(prefix)).Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    async Task<Dictionary<string, string>> Metadata(string key, ICloudFilesService? files = null) =>
        new(await (files ?? store).GetMetadataAsync(key), StringComparer.OrdinalIgnoreCase);

    async Task<AdapterManifest?> PackagedManifest(string key, ICloudFilesService? files = null)
    {
        var zip = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        await using (var remote = await (files ?? store).OpenReadAsync(key))
        await using (var local = File.Create(zip))
            await remote.CopyToAsync(local);
        return AdapterRepository.ReadManifest(zip);
    }

    async Task<string> Sha256Of(string key, ICloudFilesService? files = null)
    {
        var zip = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        await using (var remote = await (files ?? store).OpenReadAsync(key))
        await using (var local = File.Create(zip))
            await remote.CopyToAsync(local);
        return InstallerLogic.Sha256Of(zip);
    }

    Task<AdapterCatalogEntry> Entry(string id, ICloudFilesService? files = null) =>
        new AdapterCatalogStore(files ?? store).GetAsync(id);

    static readonly string[] LegacyKeys = { "EntryAssembly", "Lang", "Timestamp", "Lifecycle", "Kind", "Sha256", "Hash" };

    static void AssertLegacyMetadata(IReadOnlyDictionary<string, string> metadata, string? version)
    {
        foreach (var key in LegacyKeys)
            Assert.IsTrue(metadata.ContainsKey(key), $"adapters/{{id}} is missing the {key} metadata an older host or listing reads");
        Assert.AreEqual(Classic + ".dll", metadata["EntryAssembly"]);
        Assert.AreEqual("dotnet", metadata["Lang"]);
        Assert.IsFalse(string.IsNullOrWhiteSpace(metadata["Hash"]), "every host requires Hash");
        Assert.AreEqual(version ?? "", metadata["Version"]);
    }

    // ---------------------------------------------------------------- manifest

    [TestMethod]
    public async Task The_author_file_is_kept_and_generated_fields_win()
    {
        const string author = """
            {
              "id": "someone.else",
              "version": "9.9.9",
              "displayName": "Order Summaries",
              "summary": "Summarises orders.",
              "publisher": { "name": "Simplify9", "url": "https://simplify9.com" },
              "license": "MIT",
              "tags": [ "orders" ],
              "kinds": [ "validator" ],
              "language": "fsharp",
              "entry": "Wrong.dll",
              "runtime": "python",
              "lifecycle": "resident",
              "releaseNotes": "from the file",
              "futureField": { "nested": [1, 2, 3] }
            }
            """;

        var result = await Publish("merge.adapter", "1.0.0", authorJson: author, notes: "from the command line");

        var manifest = await PackagedManifest("adapters/merge.adapter");
        Assert.IsNotNull(manifest);

        // The author's presentation survives.
        Assert.AreEqual("Order Summaries", manifest!.DisplayName);
        Assert.AreEqual("Summarises orders.", manifest.Summary);
        Assert.AreEqual("Simplify9", manifest.Publisher.Name);
        Assert.AreEqual("MIT", manifest.License);
        CollectionAssert.AreEqual(new[] { "orders" }, manifest.Tags);
        CollectionAssert.AreEqual(new[] { "validator" }, manifest.Kinds, "author-declared kinds win over the described ones");
        Assert.AreEqual("fsharp", manifest.Language);
        Assert.IsTrue(manifest.Extensions!.ContainsKey("futureField"), "a field this model does not know was dropped");
        Assert.AreEqual(3, manifest.Extensions["futureField"].GetProperty("nested").GetArrayLength());

        // The facts are the installer's.
        Assert.AreEqual("merge.adapter", manifest.Id);
        Assert.AreEqual("1.0.0", manifest.Version);
        Assert.AreEqual(Classic + ".dll", manifest.Entry);
        Assert.AreEqual("dotnet", manifest.Runtime);
        Assert.AreEqual(AdapterManifest.ClassicLifecycle, manifest.Lifecycle);
        Assert.IsNull(manifest.Protocol);
        Assert.AreEqual("from the command line", manifest.ReleaseNotes);
        Assert.IsNotNull(manifest.PublishedOn);
        Assert.IsTrue(AdapterCatalogPaths.IsVersion(manifest.SdkVersion), $"sdkVersion '{manifest.SdkVersion}'");

        // And the metadata agrees with the manifest.
        Assert.AreEqual("validator", (await Metadata("adapters/merge.adapter"))["Kind"]);
        Assert.AreEqual(result.Sha256, (await Entry("merge.adapter")).Sha256);
    }

    [TestMethod]
    public async Task Without_an_author_file_kinds_are_described_and_kind_flag_wins()
    {
        var described = await Publish("described.adapter", null);
        CollectionAssert.AreEquivalent(new[] { "handler", "mapper" }, described.Manifest.Kinds);
        Assert.AreEqual("csharp", described.Manifest.Language);
        Assert.IsNull(described.Manifest.Version);

        var overridden = await Publish("overridden.adapter", null, kind: "receiver, handler");
        CollectionAssert.AreEqual(new[] { "receiver", "handler" }, overridden.Manifest.Kinds);
        Assert.AreEqual("receiver,handler", (await Metadata("adapters/overridden.adapter"))["Kind"]);
    }

    [TestMethod]
    public async Task A_resident_adapter_is_not_probed_and_speaks_protocol_2()
    {
        var result = await Publish("resident.adapter", "1.0.0", project: Resident, probe: true);

        Assert.AreEqual(AdapterManifest.ResidentLifecycle, result.Manifest.Lifecycle);
        Assert.AreEqual(2, result.Manifest.Protocol.Min);
        Assert.AreEqual(2, result.Manifest.Protocol.Max);
        Assert.AreEqual(0, result.Manifest.Properties.Count);
        Assert.AreEqual("resident", (await Metadata("adapters/resident.adapter"))["Lifecycle"]);
        Assert.IsFalse(output.Any(l => l.StartsWith("Warning")), string.Join("\n", output));
    }

    [TestMethod]
    public async Task An_invalid_manifest_blocks_the_publish()
    {
        const string author = """
            { "properties": [ { "name": "Colour", "type": "colour" }, { "name": "Colour" } ] }
            """;

        var ex = await Assert.ThrowsExceptionAsync<SWException>(() => Publish("invalid.adapter", "1.0.0", authorJson: author));
        StringAssert.Contains(ex.Message, "unknown type 'colour'");
        StringAssert.Contains(ex.Message, "declared twice");

        CollectionAssert.AreEqual(Array.Empty<string>(), await Keys(), "nothing may be uploaded for an invalid manifest");
    }

    [TestMethod]
    public async Task An_unreadable_author_file_blocks_the_publish()
    {
        await Assert.ThrowsExceptionAsync<SWException>(() => Publish("broken.adapter", null, authorJson: "{ not json"));
        CollectionAssert.AreEqual(Array.Empty<string>(), await Keys());
    }

    [TestMethod]
    public async Task A_missing_icon_blocks_the_publish()
    {
        var ex = await Assert.ThrowsExceptionAsync<SWException>(
            () => Publish("noicon.adapter", "1.0.0", authorJson: """{ "icon": "assets/icon.png" }"""));
        StringAssert.Contains(ex.Message, "assets/icon.png");
        CollectionAssert.AreEqual(Array.Empty<string>(), await Keys());
    }

    [TestMethod]
    public async Task A_small_icon_is_inlined_in_the_catalog_and_a_large_one_is_not()
    {
        var publish = PublishDirectory(Classic);
        Directory.CreateDirectory(Path.Combine(publish, "assets"));
        var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(publish, "assets", "icon.png"), icon);

        await Publish("icon.adapter", "1.0.0", authorJson: """{ "icon": "assets/icon.png" }""", publishDirectory: publish);

        var entry = await Entry("icon.adapter");
        Assert.AreEqual("data:image/png;base64," + Convert.ToBase64String(icon), entry.IconDataUri);
        Assert.AreEqual("assets/icon.png", entry.Manifest.Icon);

        var large = PublishDirectory(Classic);
        File.WriteAllBytes(Path.Combine(large, "icon.svg"), new byte[ManifestBuilder.MaxInlineIconBytes + 1]);
        await Publish("bigicon.adapter", "1.0.0", authorJson: """{ "icon": "icon.svg" }""", publishDirectory: large);

        Assert.IsNull((await Entry("bigicon.adapter")).IconDataUri);
        Assert.IsTrue(output.Any(l => l.StartsWith("Warning") && l.Contains("icon.svg")), "a skipped icon should be a warning");
    }

    // ---------------------------------------------------------------- probe

    [TestMethod]
    public async Task The_probe_maps_what_the_adapter_expects()
    {
        var result = await Publish("probed.adapter", null, probe: true);
        var properties = result.Manifest.Properties.ToDictionary(p => p.Name);

        CollectionAssert.AreEqual(new[] { "BaseUrl", "ApiKey", "TimeoutSeconds" },
            result.Manifest.Properties.Select(p => p.Name).ToList(), "declaration order is kept");

        Assert.IsFalse(properties["BaseUrl"].Required);
        Assert.AreEqual("https://api.example.test", properties["BaseUrl"].Default);
        Assert.AreEqual(AdapterProperty.TextType, properties["BaseUrl"].Type);

        Assert.IsTrue(properties["ApiKey"].Required);
        Assert.IsTrue(properties["ApiKey"].Secret);
        Assert.IsNull(properties["ApiKey"].Default);

        Assert.AreEqual("30", properties["TimeoutSeconds"].Default);
        Assert.IsFalse(properties["TimeoutSeconds"].Secret);

        var packaged = await PackagedManifest("adapters/probed.adapter");
        Assert.AreEqual(3, packaged!.Properties.Count);
    }

    [TestMethod]
    public async Task Author_properties_are_used_instead_of_probing()
    {
        var result = await Publish("declared.adapter", null, probe: true,
            authorJson: """{ "properties": [ { "name": "Region", "type": "select", "options": ["eu", "us"], "required": true } ] }""");

        Assert.AreEqual(1, result.Manifest.Properties.Count);
        Assert.AreEqual("Region", result.Manifest.Properties[0].Name);
        Assert.AreEqual(AdapterProperty.SelectType, result.Manifest.Properties[0].Type);
    }

    [TestMethod]
    public async Task A_failed_probe_is_a_warning_not_a_failed_publish()
    {
        var publish = PublishDirectory(Classic);
        // Without its runtimeconfig the assembly cannot be started by dotnet.
        File.Delete(Path.Combine(publish, Classic + ".runtimeconfig.json"));

        var result = await Publish("unprobed.adapter", null, probe: true, publishDirectory: publish);

        Assert.AreEqual(0, result.Manifest.Properties.Count);
        Assert.IsTrue(output.Any(l => l.StartsWith("Warning") && l.Contains("startup values")), string.Join("\n", output));
        Assert.IsTrue((await Keys()).Contains("adapters/unprobed.adapter"));
    }

    [TestMethod]
    public void The_sdk_answer_maps_to_properties()
    {
        var properties = ExpectedValuesProbe.ToProperties("""
            {"User":{"Optional":true,"Default":"admin","Type":"text","Private":false,"Description":"Who"},
             "Password":{"Optional":false,"Default":null,"Type":"text","Private":true},
             "Odd":{"Type":"weird","Future":1}}
            """);

        Assert.AreEqual(3, properties.Count);
        Assert.IsFalse(properties[0].Required);
        Assert.AreEqual("admin", properties[0].Default);
        Assert.AreEqual("Who", properties[0].Description);
        Assert.IsTrue(properties[1].Required);
        Assert.IsTrue(properties[1].Secret);
        Assert.AreEqual(AdapterProperty.TextType, properties[2].Type);
        Assert.AreEqual(0, ExpectedValuesProbe.ToProperties("{{null}}").Count);
    }

    // ---------------------------------------------------------------- versioned publishing

    [TestMethod]
    public async Task A_versioned_publish_writes_the_version_the_current_package_and_the_catalog()
    {
        var result = await Publish("versioned.adapter", "1.0.0");

        CollectionAssert.AreEqual(new[]
        {
            "adapters-catalog/versioned.adapter.json",
            "adapters-versions/versioned.adapter/1.0.0",
            "adapters/versioned.adapter",
        }, await Keys());

        var current = await Metadata("adapters/versioned.adapter");
        AssertLegacyMetadata(current, "1.0.0");
        Assert.AreEqual(result.Sha256, current["Sha256"]);
        Assert.AreEqual(result.Sha256, await Sha256Of("adapters/versioned.adapter"));
        AssertLegacyMetadata(await Metadata("adapters-versions/versioned.adapter/1.0.0"), "1.0.0");

        var entry = await Entry("versioned.adapter");
        Assert.AreEqual("1.0.0", entry.Current);
        Assert.AreEqual(result.Sha256, entry.Sha256);
        Assert.AreEqual("1.0.0", entry.Manifest.Version);
        Assert.AreEqual(1, entry.Versions.Count);
        Assert.AreEqual(result.Sha256, entry.Versions[0].Sha256);
        Assert.AreEqual("tester", entry.Versions[0].PublishedBy);
        Assert.AreEqual("1.0.0", entry.Versions[0].Manifest.Version);
    }

    [TestMethod]
    public async Task Bumps_count_the_versions_already_published()
    {
        await Publish("bump.adapter", "patch");
        await Publish("bump.adapter", "minor");
        var third = await Publish("bump.adapter", "patch");

        Assert.AreEqual("1.1.1", third.Version);
        CollectionAssert.AreEqual(new[] { "1.0.0", "1.1.0", "1.1.1" },
            (await Entry("bump.adapter")).Versions.Select(v => v.Version).ToList());

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => Publish("bump.adapter", "1.1.0"));
    }

    [TestMethod]
    public async Task No_promote_leaves_the_running_package_alone()
    {
        var first = await Publish("staged.adapter", "1.0.0");
        var second = await Publish("staged.adapter", "1.1.0", promote: false);

        var current = await Metadata("adapters/staged.adapter");
        Assert.AreEqual("1.0.0", current["Version"]);
        Assert.AreEqual(first.Sha256, await Sha256Of("adapters/staged.adapter"));

        var entry = await Entry("staged.adapter");
        Assert.AreEqual("1.0.0", entry.Current);
        Assert.AreEqual(first.Sha256, entry.Sha256);
        Assert.AreEqual(2, entry.Versions.Count);
        Assert.AreEqual(second.Sha256, entry.Find("1.1.0").Sha256);
        Assert.IsTrue((await Keys()).Contains("adapters-versions/staged.adapter/1.1.0"));
    }

    [TestMethod]
    public async Task Promote_and_rollback_swap_the_running_package_and_the_catalog()
    {
        var v1 = await Publish("swap.adapter", "1.0.0");
        var v2 = await Publish("swap.adapter", "1.1.0");
        Assert.AreEqual(v2.Sha256, await Sha256Of("adapters/swap.adapter"));

        var repository = new AdapterRepository(store, output.Add);

        // Roll back.
        await repository.PromoteAsync("swap.adapter", "1.0.0", Path.Combine(root, "work"));
        Assert.AreEqual(v1.Sha256, await Sha256Of("adapters/swap.adapter"));
        var metadata = await Metadata("adapters/swap.adapter");
        AssertLegacyMetadata(metadata, "1.0.0");
        Assert.AreEqual(v1.Sha256, metadata["Sha256"]);
        var entry = await Entry("swap.adapter");
        Assert.AreEqual("1.0.0", entry.Current);
        Assert.AreEqual(v1.Sha256, entry.Sha256);
        Assert.AreEqual("1.0.0", entry.Manifest.Version);

        // And forward again.
        await repository.PromoteAsync("swap.adapter", "1.1.0", Path.Combine(root, "work"));
        Assert.AreEqual(v2.Sha256, await Sha256Of("adapters/swap.adapter"));
        Assert.AreEqual("1.1.0", (await Entry("swap.adapter")).Current);
        Assert.AreEqual(2, (await Entry("swap.adapter")).Versions.Count, "promoting adds no history");
    }

    [TestMethod]
    public async Task Promote_carries_the_icon_of_the_promoted_version()
    {
        var publish = PublishDirectory(Classic);
        File.WriteAllBytes(Path.Combine(publish, "icon.png"), new byte[] { 1, 2, 3 });
        await Publish("iconswap.adapter", "1.0.0", authorJson: """{ "icon": "icon.png" }""", publishDirectory: publish);
        await Publish("iconswap.adapter", "1.1.0");
        Assert.IsNull((await Entry("iconswap.adapter")).IconDataUri);

        await new AdapterRepository(store, output.Add).PromoteAsync("iconswap.adapter", "1.0.0", Path.Combine(root, "work"));
        Assert.AreEqual("data:image/png;base64,AQID", (await Entry("iconswap.adapter")).IconDataUri);
    }

    [TestMethod]
    public async Task Promote_refuses_unknown_withdrawn_and_altered_versions()
    {
        var objects = new ObjectStore();
        await Publish("guarded.adapter", "1.0.0", files: objects);
        await Publish("guarded.adapter", "1.1.0", files: objects);
        await Publish("guarded.adapter", "1.2.0", files: objects);
        var repository = new AdapterRepository(objects, output.Add);
        var work = Path.Combine(root, "work");

        var unknown = await Assert.ThrowsExceptionAsync<SWException>(() => repository.PromoteAsync("guarded.adapter", "9.0.0", work));
        StringAssert.Contains(unknown.Message, "no published version");

        await repository.WithdrawAsync("guarded.adapter", "1.0.0");
        var withdrawn = await Assert.ThrowsExceptionAsync<SWException>(() => repository.PromoteAsync("guarded.adapter", "1.0.0", work));
        StringAssert.Contains(withdrawn.Message, "withdrawn");

        objects.Tamper("adapters-versions/guarded.adapter/1.1.0");
        var altered = await Assert.ThrowsExceptionAsync<SWException>(() => repository.PromoteAsync("guarded.adapter", "1.1.0", work));
        StringAssert.Contains(altered.Message, "does not match");

        Assert.AreEqual("1.2.0", (await Entry("guarded.adapter", objects)).Current, "a refused promote changes nothing");
        Assert.AreEqual("1.2.0", (await Metadata("adapters/guarded.adapter", objects))["Version"]);
    }

    [TestMethod]
    public async Task Withdraw_refuses_the_current_version_and_marks_others()
    {
        await Publish("withdrawn.adapter", "1.0.0");
        await Publish("withdrawn.adapter", "1.1.0");
        var repository = new AdapterRepository(store, output.Add);

        var current = await Assert.ThrowsExceptionAsync<SWException>(() => repository.WithdrawAsync("withdrawn.adapter", "1.1.0"));
        StringAssert.Contains(current.Message, "current version");
        await Assert.ThrowsExceptionAsync<SWException>(() => repository.WithdrawAsync("withdrawn.adapter", "3.0.0"));

        await repository.WithdrawAsync("withdrawn.adapter", "1.0.0");
        await repository.WithdrawAsync("withdrawn.adapter", "1.0.0"); // twice is fine

        var entry = await Entry("withdrawn.adapter");
        Assert.IsTrue(entry.Find("1.0.0").Withdrawn);
        Assert.IsFalse(entry.Find("1.1.0").Withdrawn);
        Assert.IsTrue((await Keys()).Contains("adapters-versions/withdrawn.adapter/1.0.0"),
            "a withdrawn package stays: a deployment may still pin it");

        var listing = await repository.ListVersionsAsync("withdrawn.adapter");
        Assert.IsTrue(listing.FromCatalog);
        Assert.IsTrue(listing.Versions.Single(v => v.Version == "1.0.0").Withdrawn);
        Assert.IsTrue(listing.Versions.Single(v => v.Version == "1.1.0").Current);
    }

    // ---------------------------------------------------------------- unversioned publishing

    [TestMethod]
    public async Task An_unversioned_publish_keeps_history_but_updates_the_manifest()
    {
        await Publish("mixed.adapter", "1.0.0");
        var unversioned = await Publish("mixed.adapter", null,
            authorJson: """{ "displayName": "Hot fix" }""");

        CollectionAssert.AreEqual(new[]
        {
            "adapters-catalog/mixed.adapter.json",
            "adapters-versions/mixed.adapter/1.0.0",
            "adapters/mixed.adapter",
        }, await Keys());

        var metadata = await Metadata("adapters/mixed.adapter");
        AssertLegacyMetadata(metadata, null);
        Assert.AreEqual(unversioned.Sha256, metadata["Sha256"]);

        var entry = await Entry("mixed.adapter");
        Assert.IsNull(entry.Current, "an unversioned package is running, so no version is current");
        Assert.AreEqual(unversioned.Sha256, entry.Sha256);
        Assert.AreEqual("Hot fix", entry.Manifest.DisplayName);
        Assert.IsNull(entry.Manifest.Version);
        Assert.AreEqual(1, entry.Versions.Count, "the history is untouched");
    }

    [TestMethod]
    public async Task No_promote_needs_a_version()
    {
        await Assert.ThrowsExceptionAsync<SWException>(() => Publish("nopromote.adapter", null, promote: false));
        Assert.AreNotEqual(Program.Success, await Program.RunAsync(
            new[] { "--no-promote", Project(), "nopromote.adapter" }.Concat(LocalFlags()).ToArray(), _ => null));
    }

    // ---------------------------------------------------------------- the older layout

    /// <summary>What an installer from before the catalog left in an object store: versions under adapters/{id}/.</summary>
    static async Task<string> SeedOldInstallerLayout(ObjectStore objects, string id, string zip, params string[] versions)
    {
        var sha = InstallerLogic.Sha256Of(zip);
        foreach (var version in versions.Append(null))
        {
            await using var stream = File.OpenRead(zip);
            await objects.WriteAsync(stream, new WriteFileSettings
            {
                Key = version == null ? $"adapters/{id}" : $"adapters/{id}/{version}",
                ContentType = "application/zip",
                Metadata = new Dictionary<string, string>
                {
                    ["EntryAssembly"] = Classic + ".dll", ["Lang"] = "dotnet", ["Lifecycle"] = "classic",
                    ["Kind"] = "handler", ["Sha256"] = sha, ["Protocol"] = "1",
                    ["Timestamp"] = $"2025-01-0{Array.IndexOf(versions, version) + 2}T00:00:00Z",
                },
            });
        }
        return sha;
    }

    string OldPackage()
    {
        var zip = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        Assert.IsTrue(new InstallerLogic().Compress(PublishDirectory(Classic), zip));
        return zip;
    }

    [TestMethod]
    public async Task Versions_falls_back_to_the_packages_without_a_catalog()
    {
        var objects = new ObjectStore();
        await SeedOldInstallerLayout(objects, "old.adapter", OldPackage(), "1.0.0", "1.1.0");

        var listing = await new AdapterRepository(objects, output.Add).ListVersionsAsync("old.adapter");

        Assert.IsFalse(listing.FromCatalog);
        CollectionAssert.AreEqual(new[] { "1.0.0", "1.1.0" }, listing.Versions.Select(v => v.Version).ToList());
        // Both versions carry the digest the current one does; the sha cannot tell them apart, and
        // both match — what matters is that the fallback recognises a current one at all.
        Assert.IsTrue(listing.Versions.Any(v => v.Current));
        Assert.IsNull(await Entry("old.adapter", objects), "listing must not write a catalog");

        var lines = Program.FormatVersions("old.adapter", listing).ToList();
        StringAssert.Contains(lines[0], "no catalog entry");
    }

    [TestMethod]
    public async Task Promote_works_on_an_adapter_published_before_the_catalog()
    {
        var objects = new ObjectStore();
        var sha = await SeedOldInstallerLayout(objects, "old.adapter", OldPackage(), "1.0.0");
        var newer = await Publish("old.adapter", "1.1.0", files: objects);

        // The new version came from adapters-versions/, the old one is still read from adapters/{id}/.
        var entry = await Entry("old.adapter", objects);
        CollectionAssert.AreEqual(new[] { "1.0.0", "1.1.0" }, entry.Versions.Select(v => v.Version).ToList());
        Assert.AreEqual(sha, entry.Find("1.0.0").Sha256);

        await new AdapterRepository(objects, output.Add).PromoteAsync("old.adapter", "1.0.0", Path.Combine(root, "work"));

        var metadata = await Metadata("adapters/old.adapter", objects);
        Assert.AreEqual(Classic + ".dll", metadata["EntryAssembly"]);
        Assert.AreEqual("1.0.0", metadata["Version"]);
        Assert.AreEqual(sha, metadata["Sha256"]);
        Assert.AreEqual("1", metadata["Protocol"], "metadata the version carried goes with it");
        Assert.AreEqual(sha, await Sha256Of("adapters/old.adapter", objects));

        entry = await Entry("old.adapter", objects);
        Assert.AreEqual("1.0.0", entry.Current);
        Assert.IsNull(entry.Manifest, "a package from before manifests has none, and none is invented");
        Assert.AreNotEqual(newer.Sha256, entry.Sha256);
    }

    [TestMethod]
    public async Task Promote_creates_the_catalog_entry_when_there_is_none()
    {
        var objects = new ObjectStore();
        var sha = await SeedOldInstallerLayout(objects, "fresh.adapter", OldPackage(), "1.0.0", "2.0.0");

        await new AdapterRepository(objects, output.Add).PromoteAsync("fresh.adapter", "1.0.0", Path.Combine(root, "work"));

        var entry = await Entry("fresh.adapter", objects);
        Assert.IsNotNull(entry);
        Assert.AreEqual("1.0.0", entry.Current);
        Assert.AreEqual(sha, entry.Sha256);
        CollectionAssert.AreEqual(new[] { "1.0.0", "2.0.0" }, entry.Versions.Select(v => v.Version).ToList());
    }

    [TestMethod]
    public async Task Version_numbers_count_versions_left_by_an_older_installer()
    {
        // On the filesystem provider an older installer's versions can only exist with no current package.
        var zip = OldPackage();
        await using (var stream = File.OpenRead(zip))
            await store.WriteAsync(stream, new WriteFileSettings
            {
                Key = "adapters/legacy.adapter/1.2.0",
                Metadata = new Dictionary<string, string> { ["EntryAssembly"] = Classic + ".dll" },
            });

        Assert.AreEqual("1.2.1", await new AdapterRepository(store).ResolveVersionAsync("legacy.adapter", "patch"));
        Assert.AreEqual("1.2.1", (await Publish("legacy.adapter", "patch", promote: false)).Version);
    }

    // ---------------------------------------------------------------- what older hosts and listings see

    /// <summary>
    /// Older listings take every key under adapters/ for an adapter, so after anything this installer
    /// does, the only key there must be adapters/{id}.
    /// </summary>
    [TestMethod]
    public async Task Nothing_new_is_written_under_adapters()
    {
        var repository = new AdapterRepository(store, output.Add);
        await Publish("tidy.adapter", "1.0.0");
        await Publish("tidy.adapter", "minor");
        await Publish("tidy.adapter", "patch", promote: false);
        await repository.PromoteAsync("tidy.adapter", "1.0.0", Path.Combine(root, "work"));
        await repository.WithdrawAsync("tidy.adapter", "1.1.0");
        await Publish("tidy.adapter", null);

        CollectionAssert.AreEqual(new[] { "adapters/tidy.adapter" }, await Keys("adapters/"));
        CollectionAssert.AreEqual(new[]
        {
            "adapters-versions/tidy.adapter/1.0.0",
            "adapters-versions/tidy.adapter/1.1.0",
            "adapters-versions/tidy.adapter/1.1.1",
        }, await Keys("adapters-versions/"));
        CollectionAssert.AreEqual(new[] { "adapters-catalog/tidy.adapter.json" }, await Keys("adapters-catalog/"));
        AssertLegacyMetadata(await Metadata("adapters/tidy.adapter"), null);
    }

    // ---------------------------------------------------------------- command line

    [TestMethod]
    public async Task The_commands_run_from_the_command_line()
    {
        await Publish("cli.adapter", "1.0.0");
        await Publish("cli.adapter", "1.1.0");

        int Run(params string[] args) => Program.RunAsync(args.Concat(LocalFlags()).ToArray(), _ => null).Result;

        Assert.AreEqual(Program.Success, Run("versions", "cli.adapter"));
        Assert.AreEqual(Program.Success, Run("promote", "cli.adapter", "1.0.0"));
        Assert.AreEqual("1.0.0", (await Entry("cli.adapter")).Current);
        Assert.AreEqual(Program.Failure, Run("withdraw", "cli.adapter", "1.0.0"), "the current version cannot be withdrawn");
        Assert.AreEqual(Program.Success, Run("withdraw", "cli.adapter", "1.1.0"));
        Assert.AreEqual(Program.Failure, Run("promote", "cli.adapter", "1.1.0"), "a withdrawn version cannot be promoted");
        Assert.AreEqual(Program.Failure, Run("promote", "cli.adapter", "7.0.0"));
        Assert.AreEqual(Program.Failure, Run("promote", "Bad/Id", "1.0.0"));
        Assert.AreEqual(Program.Failure, Run("promote", "cli.adapter"), "a missing version is a bad command line");
        Assert.AreEqual(Program.Success, Run("versions", "never.published"));
    }

    [TestMethod]
    public void Published_by_falls_back_from_flag_to_environment_to_user()
    {
        Assert.AreEqual("flag", UploadOptionsResolver.ResolvePublishedBy("flag", _ => "env"));
        Assert.AreEqual("swsl", UploadOptionsResolver.ResolvePublishedBy(null,
            n => n == "SWSL_PUBLISHED_BY" ? "swsl" : n == "GITHUB_ACTOR" ? "actor" : null));
        Assert.AreEqual("actor", UploadOptionsResolver.ResolvePublishedBy(null, n => n == "GITHUB_ACTOR" ? "actor" : null));
        Assert.AreEqual(Environment.UserName, UploadOptionsResolver.ResolvePublishedBy(null, _ => null));
    }

    /// <summary>
    /// A deployment whose hosts read adapters from another folder: publishing, locating and promoting
    /// all happen there, and nothing lands under the default one.
    /// </summary>
    [TestMethod]
    public async Task A_repository_and_a_publish_work_under_the_folder_they_are_given()
    {
        var project = Path.Combine(root, "pyproject");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "adapter.json"), """{ "id": "acme.py", "version": "1.0.0", "runtime": "python", "entry": "main.py" }""");
        File.WriteAllText(Path.Combine(project, "main.py"), """
            import sw_serverless as sw

            class Echo:
                @sw.command("Echo")
                def echo(self, text: str) -> str:
                    return text

            sw.run(Echo)
            """);
        var built = await Tooling.Building.PackageBuilder.BuildAsync(new Tooling.Building.BuildRequest
        {
            ProjectDirectory = project,
            OutputDirectory = Path.Combine(root, "pybuilt"),
        });
        Assert.IsTrue(built.Succeeded, string.Join("; ", built.Problems));

        var published = await PackagePublisher.PublishPackageAsync(store, new PublishPackageRequest
        {
            PackagePath = built.ZipPath,
            Promote = false,
            RemotePath = "custom",
        }, output.Add);
        Assert.AreEqual("1.0.0", published.Version);

        var keys = await Keys();
        CollectionAssert.Contains(keys, "custom-versions/acme.py/1.0.0");
        CollectionAssert.Contains(keys, "custom-catalog/acme.py.json");
        Assert.IsFalse(keys.Any(k => k.StartsWith("adapters")), string.Join(", ", keys));

        var repository = new AdapterRepository(store, output.Add, "custom");
        Assert.AreEqual("custom", repository.RemotePath);
        Assert.IsNull((await repository.LoadEntryAsync("acme.py")).Current, "published without being made current");
        CollectionAssert.AreEqual(new[] { "1.0.0" }, (await repository.LocateVersionsAsync("acme.py")).Keys.ToArray());

        await repository.PromoteAsync("acme.py", "1.0.0", Path.Combine(root, "promote"));
        Assert.AreEqual("1.0.0", (await repository.LoadEntryAsync("acme.py")).Current);
        // A Python adapter is never put where hosts before manifests would run it with dotnet.
        Assert.IsFalse((await Keys()).Contains("custom/acme.py"));
    }
}
