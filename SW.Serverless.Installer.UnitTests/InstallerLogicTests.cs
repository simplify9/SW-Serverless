using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Installer.Shared;

namespace SW.Serverless.Installer.UnitTests;

[TestClass]
public class SemverEdgeCaseTests
{
    [TestMethod]
    public void Pre_release_keys_are_ignored_when_bumping()
    {
        var version = Semver.GetNewVersion("patch", new List<string> { "1.0.0", "2.0.0-beta", "1.0.1-rc.1" });
        Assert.AreEqual("1.0.1", version);
    }

    [TestMethod]
    public void Only_pre_release_keys_counts_as_no_versions()
    {
        Assert.AreEqual("1.0.0", Semver.GetNewVersion("minor", new List<string> { "1.0.0-beta" }));
    }

    [TestMethod]
    public void Keys_that_are_not_versions_at_all_do_not_throw()
    {
        var junk = new List<string> { "foo", "latest", "", "1.2", "99999999999.0.0" };

        Assert.AreEqual("1.0.0", Semver.GetNewVersion("major", junk));
        Assert.AreEqual("3.1.4", Semver.GetNewVersion("3.1.4", junk));
    }

    [TestMethod]
    public void A_higher_pre_release_can_be_published_explicitly()
    {
        Assert.AreEqual("1.3.0-rc.1", Semver.GetNewVersion("1.3.0-rc.1", new List<string> { "1.2.5" }));
    }

    [TestMethod]
    public void A_pre_release_of_the_highest_release_is_rejected()
    {
        Assert.ThrowsException<ArgumentException>(
            () => Semver.GetNewVersion("1.2.5-rc.1", new List<string> { "1.2.5" }));
    }

    [TestMethod]
    public void An_existing_pre_release_is_not_overwritten()
    {
        Assert.ThrowsException<ArgumentException>(
            () => Semver.GetNewVersion("2.0.0-beta", new List<string> { "1.0.0", "2.0.0-beta" }));
    }

    /// <summary>A typo used to publish 1.0.0 when the folder was empty.</summary>
    [TestMethod]
    public void An_invalid_mode_is_rejected_even_with_no_versions()
    {
        Assert.ThrowsException<ArgumentException>(() => Semver.GetNewVersion("mjaor", new List<string>()));
        Assert.ThrowsException<ArgumentException>(() => Semver.GetNewVersion("mjaor", new List<string> { "1.0.0" }));
    }

    [TestMethod]
    public void Bumps_are_case_insensitive()
    {
        Assert.AreEqual("1.0.4", Semver.GetNewVersion("PATCH", new List<string> { "1.0.3" }));
    }
}

[TestClass]
public class AdapterIdTests
{
    [DataTestMethod]
    [DataRow("my.adapter")]
    [DataRow("a-b_c")]
    [DataRow("infolink.carrier.v2")]
    [DataRow("0x")]
    public void Valid_ids_are_accepted(string id) => Assert.IsTrue(InstallerLogic.IsValidAdapterId(id));

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Upper")]
    [DataRow("a/b")]
    [DataRow("a\\b")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow(".hidden")]
    [DataRow("-flag")]
    [DataRow("a b")]
    [DataRow("a:b")]
    [DataRow("../escape")]
    public void Invalid_ids_are_rejected(string? id) => Assert.IsFalse(InstallerLogic.IsValidAdapterId(id));

    /// <summary>
    /// Listing "adapters/foo" without the slash also matched "adapters/foobar/..." and the
    /// legacy "adapters/foo" object itself.
    /// </summary>
    [TestMethod]
    public void Existing_versions_are_only_those_directly_under_the_adapter()
    {
        var keys = new[]
        {
            "adapters/foo/1.0.0", "adapters/foo/1.0.1", "adapters/foobar/9.0.0", "adapters/foo",
            "adapters/foo/sub/2.0.0",
        };

        CollectionAssert.AreEqual(
            new[] { "1.0.0", "1.0.1" },
            InstallerLogic.ExistingVersions("adapters/foo", keys));
    }
}

[TestClass]
public class UploadOptionsResolverTests
{
    private static Func<string, string?> Env(Dictionary<string, string> values) =>
        name => values.TryGetValue(name, out var value) ? value : null;

    private static CliOptions Cli(Action<CliOptions>? configure = null)
    {
        var options = new CliOptions { ProjectPath = "a.csproj", AdapterId = "my.adapter", Version = "patch" };
        configure?.Invoke(options);
        return options;
    }

    [TestMethod]
    public void Credentials_fall_back_to_environment_variables()
    {
        var resolved = UploadOptionsResolver.Resolve(Cli(), null, Env(new()
        {
            ["SWSL_PROVIDER"] = "s3",
            ["SWSL_ACCESS_KEY"] = "env-key",
            ["SWSL_SECRET_KEY"] = "env-secret",
            ["SWSL_BUCKET"] = "env-bucket",
            ["SWSL_SERVICE_URL"] = "https://s3.example",
            ["SWSL_REGION"] = "me-central-1",
        }));

        Assert.AreEqual("s3", resolved.Provider);
        Assert.AreEqual("env-key", resolved.AccessKeyId);
        Assert.AreEqual("env-secret", resolved.SecretAccessKey);
        Assert.AreEqual("env-bucket", resolved.BucketName);
        Assert.AreEqual("https://s3.example", resolved.ServiceUrl);
        Assert.AreEqual("me-central-1", resolved.Region);
        Assert.AreEqual("my.adapter", resolved.AdapterId);
        Assert.AreEqual("patch", resolved.Version);
    }

    [TestMethod]
    public void Flags_win_over_the_config_file_which_wins_over_the_environment()
    {
        const string config = """{ "CloudFiles": { "AccessKeyId": "file-key", "SecretAccessKey": "file-secret", "BucketName": "file-bucket" } }""";

        var resolved = UploadOptionsResolver.Resolve(
            Cli(o => o.AccessKeyId = "flag-key"),
            config,
            Env(new() { ["SWSL_ACCESS_KEY"] = "env-key", ["SWSL_SECRET_KEY"] = "env-secret", ["SWSL_SERVICE_URL"] = "env-url" }));

        Assert.AreEqual("flag-key", resolved.AccessKeyId);
        Assert.AreEqual("file-secret", resolved.SecretAccessKey);
        Assert.AreEqual("file-bucket", resolved.BucketName);
        Assert.AreEqual("env-url", resolved.ServiceUrl);
    }

    [TestMethod]
    public void Google_cloud_settings_are_mapped_from_the_config_file()
    {
        const string config = """
            { "CloudFiles": {
                "Provider": "gc", "BucketName": "adapters",
                "ProjectId": "proj", "PrivateKeyId": "kid", "PrivateKey": "-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----\n",
                "ClientEmail": "svc@proj.iam.gserviceaccount.com", "ClientId": "123",
                "ClientX509CertUrl": "https://www.googleapis.com/robot/v1/metadata/x509/svc"
            } }
            """;

        var resolved = UploadOptionsResolver.Resolve(Cli(), config, Env(new()));

        Assert.AreEqual("gc", resolved.Provider);
        Assert.AreEqual("proj", resolved.ProjectId);
        Assert.AreEqual("kid", resolved.PrivateKeyId);
        StringAssert.StartsWith(resolved.PrivateKey, "-----BEGIN PRIVATE KEY-----\nabc");
        Assert.AreEqual("svc@proj.iam.gserviceaccount.com", resolved.ClientEmail);
        Assert.AreEqual("123", resolved.ClientId);
        Assert.AreEqual("https://www.googleapis.com/robot/v1/metadata/x509/svc", resolved.ClientX509CertUrl);
    }

    [TestMethod]
    public void Google_cloud_settings_fall_back_to_the_environment()
    {
        var resolved = UploadOptionsResolver.Resolve(Cli(o => o.Provider = "gc"), null, Env(new()
        {
            ["SWSL_GC_PROJECT_ID"] = "proj",
            ["SWSL_GC_PRIVATE_KEY_ID"] = "kid",
            // As copied out of a service-account JSON file: the newlines are literal "\n".
            ["SWSL_GC_PRIVATE_KEY"] = "-----BEGIN PRIVATE KEY-----\\nabc\\n-----END PRIVATE KEY-----\\n",
            ["SWSL_GC_CLIENT_EMAIL"] = "svc@proj.iam.gserviceaccount.com",
            ["SWSL_GC_CLIENT_ID"] = "123",
        }));

        Assert.AreEqual("gc", resolved.Provider);
        Assert.AreEqual("proj", resolved.ProjectId);
        Assert.AreEqual("kid", resolved.PrivateKeyId);
        Assert.AreEqual("-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----\n", resolved.PrivateKey);
        Assert.AreEqual("svc@proj.iam.gserviceaccount.com", resolved.ClientEmail);
        Assert.AreEqual("123", resolved.ClientId);
        Assert.IsNull(resolved.ClientX509CertUrl);
    }

    [TestMethod]
    public void An_empty_config_file_is_an_error()
    {
        Assert.ThrowsException<SW.PrimitiveTypes.SWException>(
            () => UploadOptionsResolver.Resolve(Cli(), "  ", Env(new())));
    }
}

[TestClass]
public class PackagingTests
{
    private string root = null!;

    [TestInitialize]
    public void Init()
    {
        root = Path.Combine(Path.GetTempPath(), "swsl-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private string Publish(params string[] files)
    {
        var publish = Path.Combine(root, "publish");
        Directory.CreateDirectory(publish);
        foreach (var file in files)
        {
            var path = Path.Combine(publish, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
        return publish;
    }

    [TestMethod]
    public void Compress_packages_everything_but_excluded_extensions()
    {
        var publish = Publish("Adapter.dll", "Adapter.pdb", "runtimes/linux-x64/native.so");
        var zip = Path.Combine(root, "adapter.zip");

        Assert.IsTrue(new InstallerLogic().Compress(publish, zip));

        using var archive = ZipFile.OpenRead(zip);
        CollectionAssert.AreEquivalent(
            new[] { "Adapter.dll", Path.Combine("runtimes", "linux-x64", "native.so") },
            archive.Entries.Select(e => e.FullName).ToList());
    }

    /// <summary>
    /// A file that cannot be read used to be skipped and the rest uploaded, which shipped an
    /// adapter missing a dependency.
    /// </summary>
    [TestMethod]
    public void Compress_fails_when_a_file_cannot_be_read()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Uses Unix file modes to make a file unreadable.");
            return;
        }

        var publish = Publish("Adapter.dll", "Dependency.dll");
        var unreadable = Path.Combine(publish, "Dependency.dll");
        File.SetUnixFileMode(unreadable, UnixFileMode.None);

        try
        {
            try { File.ReadAllBytes(unreadable); Assert.Inconclusive("Running as a user that can read anything."); }
            catch (UnauthorizedAccessException) { }

            var zip = Path.Combine(root, "adapter.zip");

            Assert.IsFalse(new InstallerLogic().Compress(publish, zip));
            Assert.IsFalse(File.Exists(zip), "a partial package was left behind");
        }
        finally
        {
            File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [TestMethod]
    public void The_entry_assembly_comes_from_the_project_assembly_name()
    {
        var publish = Publish("Custom.Name.dll", "Proj.dll");
        var project = Path.Combine(root, "Proj.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>Custom.Name</AssemblyName>
              </PropertyGroup>
            </Project>
            """);

        Assert.AreEqual("Custom.Name", InstallerLogic.AssemblyNameFromProjectFile(project));
        Assert.AreEqual("Custom.Name.dll", InstallerLogic.ResolveEntryAssembly(publish, project));
    }

    [TestMethod]
    public void The_entry_assembly_falls_back_to_the_project_file_name()
    {
        var publish = Publish("Proj.dll");
        var project = Path.Combine(root, "Proj.csproj");
        File.WriteAllText(project, "<Project />");

        Assert.AreEqual("Proj.dll", InstallerLogic.ResolveEntryAssembly(publish, project));
    }

    [TestMethod]
    public void A_missing_entry_assembly_is_null()
    {
        var publish = Publish("Other.dll", "Another.dll");
        var project = Path.Combine(root, "Proj.csproj");
        File.WriteAllText(project, "<Project />");

        Assert.IsNull(InstallerLogic.ResolveEntryAssembly(publish, project));
    }

    /// <summary>
    /// End to end against the filesystem provider: versions are counted per adapter only, and every
    /// upload carries the SHA-256 of its zip.
    /// </summary>
    [TestMethod]
    public async Task Versioned_upload_counts_only_its_own_adapter_and_records_the_hash()
    {
        var publish = Publish("Adapter.dll");
        var zip = Path.Combine(root, "adapter.zip");
        Assert.IsTrue(new InstallerLogic().Compress(publish, zip));

        ServerlessUploadOptions Options(string id, string version) => new()
        {
            Provider = "local",
            BucketName = "bucket",
            ServiceUrl = Path.Combine(root, "store"),
            AdapterId = id,
            Version = version,
        };

        var installer = new InstallerLogic();
        Assert.IsTrue(await installer.PushToCloud(zip, "Adapter.dll", Options("foobar", "5.0.0")));
        Assert.IsTrue(await installer.PushToCloud(zip, "Adapter.dll", Options("foo", "patch")));
        Assert.IsTrue(await installer.PushToCloud(zip, "Adapter.dll", Options("foo", "patch")));

        var store = CloudFilesFactory.Create(Options("foo", null!));
        var keys = (await store.ListAsync("adapters/")).Select(f => f.Key).OrderBy(k => k).ToList();
        CollectionAssert.AreEqual(
            new[] { "adapters/foo/1.0.0", "adapters/foo/1.0.1", "adapters/foobar/5.0.0" }, keys);

        var metadata = await store.GetMetadataAsync("adapters/foo/1.0.1");
        Assert.AreEqual(InstallerLogic.Sha256Of(zip), metadata["Sha256"]);
        Assert.AreEqual(64, metadata["Sha256"].Length);
    }

    [TestMethod]
    public async Task An_invalid_adapter_id_is_not_uploaded()
    {
        var publish = Publish("Adapter.dll");
        var zip = Path.Combine(root, "adapter.zip");
        Assert.IsTrue(new InstallerLogic().Compress(publish, zip));

        Assert.IsFalse(await new InstallerLogic().PushToCloud(zip, "Adapter.dll", new ServerlessUploadOptions
        {
            Provider = "local", BucketName = "bucket", ServiceUrl = Path.Combine(root, "store"),
            AdapterId = "../escape",
        }));
    }
}

[TestClass]
public class ExitCodeTests
{
    private static Task<int> Run(params string[] args) => Program.RunAsync(args, _ => null);

    [TestMethod]
    public async Task No_arguments_is_a_failure() => Assert.AreNotEqual(0, await Run());

    [TestMethod]
    public async Task An_unknown_option_is_a_failure() =>
        Assert.AreNotEqual(0, await Run("--bogus", "a.csproj", "my.adapter"));

    [TestMethod]
    public async Task Help_is_not_a_failure() => Assert.AreEqual(0, await Run("--help"));

    [TestMethod]
    public async Task An_invalid_adapter_id_is_a_failure() =>
        Assert.AreNotEqual(0, await Run("a.csproj", "Bad/Id"));

    [TestMethod]
    public async Task A_missing_project_is_a_failure() =>
        Assert.AreNotEqual(0, await Run(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csproj"), "my.adapter"));

    /// <summary>
    /// The parser's own --version used to shadow ours: this printed the tool version and exited 0
    /// without publishing anything.
    /// </summary>
    [TestMethod]
    public async Task Long_version_option_is_ours_not_the_parsers() =>
        Assert.AreNotEqual(0, await Run("--version", "minor", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csproj"), "my.adapter"));
}
