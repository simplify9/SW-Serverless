using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.PrimitiveTypes;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Tooling;

namespace SW.Serverless.CompatibilityTests;

/// <summary>
/// The package layout multi-language adapters bring, against the hosts already deployed:
/// a package that carries its source runs on old and new hosts alike, and only new hosts leave
/// the source unpacked; an adapter in another runtime is kept out of old hosts' sight entirely —
/// they only ever run adapters/{id}, and always with dotnet — while new hosts find it in the catalog.
/// </summary>
[TestClass]
public class PackageLayoutTests
{
    const string Sample = "SW.Serverless.Samples.Classic";

    /// <summary>The sample's build output as a zip, with an adapter.json and any extra entries.</summary>
    static string Package(AdapterManifest manifest, IDictionary<string, string>? extra = null)
    {
        var output = Compat.BuildOutput(Sample);
        var zip = Path.Combine(Path.GetTempPath(), "swsl-compat", $"layout-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
                archive.CreateEntryFromFile(file, Path.GetRelativePath(output, file).Replace('\\', '/'), CompressionLevel.Fastest);
            using (var writer = new StreamWriter(archive.CreateEntry(AdapterManifest.FileName).Open()))
                writer.Write(manifest.ToJson());
            foreach (var (name, content) in extra ?? new Dictionary<string, string>())
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open()))
                    writer.Write(content);
        }
        return zip;
    }

    static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    static AdapterManifest DotnetWithSource(string id) => new()
    {
        Id = id,
        Entry = Sample + ".dll",
        Source = new AdapterSource { Files = { ["Program.cs"] = Sha256("// the source"), ["Sample.csproj"] = Sha256("<Project />") } },
    };

    static AdapterManifest Python(string id) => new()
    {
        Id = id,
        Runtime = AdapterManifest.PythonRuntime,
        Language = "python",
        Entry = "main.py",
        Kinds = { "handler" },
    };

    static AdapterRepository Repository(Bucket bucket) => new(bucket.Files, _ => { });

    [TestMethod]
    public async Task A_package_carrying_its_source_runs_on_old_and_new_hosts_and_new_ones_leave_the_source_packed()
    {
        using var bucket = new Bucket();
        const string id = "layout.withsource";
        var zip = Package(DotnetWithSource(id), new Dictionary<string, string>
        {
            ["source/Program.cs"] = "// the source",
            ["source/Sample.csproj"] = "<Project />",
        });
        await Repository(bucket).PublishVersionAsync(id, "1.0.0", zip,
            new PackageInfo { EntryAssembly = Sample + ".dll", Manifest = DotnetWithSource(id) }, promote: true, "compat");

        var old = await Compat.RunOldHostAsync(bucket, id, "--command", "Echo", "--input", "carried");
        Assert.AreEqual(0, old.ExitCode, old.ToString());
        Assert.AreEqual("carried", old.Value("RESULT"), "a host on the released 10.0.0 runs it as before");

        using var host = Compat.NewHost(bucket);
        using (var service = (ServerlessService)host.GetRequiredService<IServerlessService>())
        {
            await service.StartAsync(id, "correlation");
            Assert.AreEqual("carried", await service.InvokeAsync<string>("Echo", "carried"));
        }

        var installed = await host.GetRequiredService<AdapterInstaller>().InstallAsync(id);
        Assert.IsTrue(File.Exists(installed.LocalPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(installed.Directory, "source")), "the source was unpacked onto the host");
    }

    [TestMethod]
    public async Task A_folder_called_source_in_a_package_without_declared_source_is_unpacked_as_before()
    {
        using var bucket = new Bucket();
        const string id = "layout.ownsource";
        var manifest = new AdapterManifest { Id = id, Entry = Sample + ".dll" };
        await Repository(bucket).PublishVersionAsync(id, "1.0.0",
            Package(manifest, new Dictionary<string, string> { ["source/needed-at-runtime.txt"] = "keep me" }),
            new PackageInfo { EntryAssembly = Sample + ".dll", Manifest = manifest }, promote: true, "compat");

        using var host = Compat.NewHost(bucket);
        var installed = await host.GetRequiredService<AdapterInstaller>().InstallAsync(id);

        Assert.AreEqual("keep me", await File.ReadAllTextAsync(Path.Combine(installed.Directory, "source", "needed-at-runtime.txt")));
    }

    [TestMethod]
    public async Task An_adapter_in_another_runtime_is_out_of_old_hosts_sight_and_new_hosts_find_its_current_version()
    {
        using var bucket = new Bucket();
        const string id = "layout.python";
        var repository = Repository(bucket);
        var package = new PackageInfo { EntryAssembly = "main.py", Manifest = Python(id) };
        await repository.PublishVersionAsync(id, "1.0.0", Package(Python(id)), package, promote: true, "compat");
        await repository.PublishVersionAsync(id, "1.1.0", Package(Python(id)), package, promote: false, "compat");

        Assert.AreEqual(0, (await bucket.KeysAsync("adapters/")).Count, "nothing under adapters/, where old hosts and old Bitween look");
        Assert.AreEqual("1.0.0", (await repository.LoadEntryAsync(id)).Current);

        var old = await Compat.RunOldHostAsync(bucket, id, "--command", "Echo", "--input", "x");
        Assert.AreNotEqual(0, old.ExitCode, "an old host must not find it, let alone start it with dotnet");

        using (var host = Compat.NewHost(bucket))
            Assert.AreEqual($"adapters-versions/{id}/1.0.0",
                (await host.GetRequiredService<AdapterInstaller>().GetMetadataAsync(id)).RemoteKey);

        await repository.PromoteAsync(id, "1.1.0", Path.Combine(bucket.Path, "..", "work-" + Guid.NewGuid().ToString("N")));

        Assert.AreEqual(0, (await bucket.KeysAsync("adapters/")).Count, "promote doesn't put it there either");
        Assert.AreEqual("1.1.0", (await repository.LoadEntryAsync(id)).Current);
        using (var host = Compat.NewHost(bucket))
            Assert.AreEqual($"adapters-versions/{id}/1.1.0",
                (await host.GetRequiredService<AdapterInstaller>().GetMetadataAsync(id)).RemoteKey);
    }

    [TestMethod]
    public async Task An_id_published_as_dotnet_cannot_move_to_another_runtime()
    {
        using var bucket = new Bucket();
        const string id = "layout.switch";
        var repository = Repository(bucket);
        await repository.PublishVersionAsync(id, "1.0.0", Package(new AdapterManifest { Id = id, Entry = Sample + ".dll" }),
            new PackageInfo { EntryAssembly = Sample + ".dll" }, promote: true, "compat");

        var refused = await Assert.ThrowsExceptionAsync<SWException>(() => repository.PublishVersionAsync(id, "2.0.0",
            Package(Python(id)), new PackageInfo { EntryAssembly = "main.py", Manifest = Python(id) }, promote: true, "compat"));
        StringAssert.Contains(refused.Message, "new id");
        Assert.AreEqual("1.0.0", (await repository.LoadEntryAsync(id)).Current);
    }

    [TestMethod]
    public async Task An_adapter_in_another_runtime_must_be_published_with_a_version()
    {
        using var bucket = new Bucket();
        var refused = await Assert.ThrowsExceptionAsync<SWException>(() => Repository(bucket).PublishUnversionedAsync("layout.unversioned",
            Package(Python("layout.unversioned")), new PackageInfo { EntryAssembly = "main.py", Manifest = Python("layout.unversioned") }));

        StringAssert.Contains(refused.Message, "with a version");
        Assert.AreEqual(0, (await bucket.KeysAsync()).Count);
    }
}
