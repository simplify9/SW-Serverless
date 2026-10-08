using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// What the installer refuses to do with a package or its metadata, whoever wrote them: write
    /// outside the adapter's own directory, or delete what something else is using.
    /// </summary>
    [TestClass]
    public class InstallerSafetyTests
    {
        static ServiceProvider services;
        static ICloudFilesService cloudFiles;
        static string localRoot;
        static string outside;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            var root = Path.Combine(Path.GetTempPath(), "swsl-installer-tests", Guid.NewGuid().ToString("N"));
            localRoot = Path.Combine(root, "installed");
            outside = Path.Combine(root, "outside");

            services = new ServiceCollection()
                .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
                    new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
                .AddMemoryCache()
                .AddLocalTestsCloudFiles(o => o.BucketName = "sw-serverless-installer-tests")
                .AddServerless(o =>
                {
                    o.AdapterRemotePath = "adapters";
                    o.AdapterLocalPath = localRoot;
                    o.AdapterMetadataCacheDuration = 1;
                })
                .BuildServiceProvider();
            cloudFiles = services.GetRequiredService<ICloudFilesService>();
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            services?.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(localRoot)!, true); } catch { }
        }

        static AdapterInstaller Installer() => new(
            services.GetRequiredService<ServerlessOptions>(),
            new MemoryCache(new MemoryCacheOptions()),
            cloudFiles);

        static async Task Publish(string adapterId, IDictionary<string, string> entries,
            string hash, string entryAssembly = "Adapter.dll")
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, content) in entries)
                {
                    var entry = zip.CreateEntry(name);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }

            buffer.Position = 0;
            await cloudFiles.WriteAsync(buffer, new WriteFileSettings
            {
                Key = $"adapters/{adapterId}",
                ContentType = "application/zip",
                Metadata = new Dictionary<string, string> { ["EntryAssembly"] = entryAssembly, ["Hash"] = hash }
            });
        }

        [TestMethod]
        public async Task An_entry_that_climbs_out_of_the_directory_is_refused_and_nothing_is_left_installed()
        {
            await Publish("zipslip", new Dictionary<string, string>
            {
                ["Adapter.dll"] = "ok",
                ["../../outside/planted.txt"] = "owned"
            }, hash: "slip1");

            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => Installer().InstallAsync("zipslip"));

            Assert.IsFalse(File.Exists(Path.Combine(outside, "planted.txt")));
            Assert.IsFalse(Directory.Exists(Path.Combine(localRoot, "slip1")),
                "a refused package must not leave a directory that would later count as installed");
        }

        [TestMethod]
        public async Task A_hash_that_is_not_a_plain_name_cannot_choose_the_directory()
        {
            await Publish("odd-hash", new Dictionary<string, string> { ["Adapter.dll"] = "ok" }, hash: "../../outside");

            var installed = await Installer().InstallAsync("odd-hash");

            Assert.IsTrue(installed.LocalPath.StartsWith(Path.GetFullPath(localRoot) + Path.DirectorySeparatorChar),
                installed.LocalPath);
            Assert.IsTrue(File.Exists(installed.LocalPath));
            Assert.IsFalse(Directory.Exists(outside));
        }

        [TestMethod]
        public async Task An_entry_assembly_outside_the_package_is_refused()
        {
            await Publish("escape-entry", new Dictionary<string, string> { ["Adapter.dll"] = "ok" },
                hash: "esc1", entryAssembly: "../../../usr/bin/env");

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Installer().InstallAsync("escape-entry"));
        }

        [TestMethod]
        public async Task A_new_version_prunes_only_its_own_adapter_and_never_a_directory_in_use()
        {
            var installer = Installer();

            // Two adapters both built as Adapter.dll: matching on that name used to let each prune the other.
            await Publish("prune-a", new Dictionary<string, string> { ["Adapter.dll"] = "a1" }, hash: "a1");
            await Publish("prune-b", new Dictionary<string, string> { ["Adapter.dll"] = "b1" }, hash: "b1");
            var a1 = await installer.InstallAsync("prune-a");
            var b1 = await installer.InstallAsync("prune-b");

            // a1 is running; a2 is published and installed.
            using (AdapterDirectoryLeases.Hold(a1.Directory))
            {
                await Publish("prune-a", new Dictionary<string, string> { ["Adapter.dll"] = "a2" }, hash: "a2");
                await Installer().InstallAsync("prune-a");

                Assert.IsTrue(Directory.Exists(a1.Directory), "a directory in use is never pruned");
                Assert.IsTrue(Directory.Exists(b1.Directory), "another adapter's directory is never pruned");
            }

            // Released: the next install of a newer version may prune it.
            await Publish("prune-a", new Dictionary<string, string> { ["Adapter.dll"] = "a3" }, hash: "a3");
            await Installer().InstallAsync("prune-a");

            Assert.IsFalse(Directory.Exists(a1.Directory));
            Assert.IsTrue(Directory.Exists(b1.Directory));
        }

        // ------------------------------------------------------------------ manifests

        static string Manifest(string json) => json;

        [TestMethod]
        public async Task A_package_without_a_manifest_runs_as_its_metadata_says()
        {
            await Publish("no-manifest", new Dictionary<string, string> { ["Adapter.dll"] = "ok" }, hash: "nm1");

            var installed = await Installer().InstallAsync("no-manifest");

            Assert.IsNull(installed.Manifest);
            Assert.AreEqual("Adapter.dll", Path.GetFileName(installed.LocalPath));
        }

        [TestMethod]
        public async Task The_manifest_says_what_to_start_when_it_disagrees_with_metadata()
        {
            await Publish("manifest-entry", new Dictionary<string, string>
            {
                ["Adapter.dll"] = "metadata says this",
                ["bin/Real.dll"] = "manifest says this",
                ["adapter.json"] = Manifest("""{ "id": "manifest-entry", "entry": "bin/Real.dll", "displayName": "Real one", "futureField": 1 }""")
            }, hash: "me1");

            var installed = await Installer().InstallAsync("manifest-entry");

            Assert.AreEqual("Real one", installed.Manifest.DisplayName);
            Assert.AreEqual("Real.dll", Path.GetFileName(installed.LocalPath));
            Assert.IsTrue(installed.Manifest.Extensions.ContainsKey("futureField"), "unknown fields are kept");
        }

        [TestMethod]
        public async Task A_manifest_entry_outside_the_package_is_refused()
        {
            await Publish("manifest-escape", new Dictionary<string, string>
            {
                ["Adapter.dll"] = "ok",
                ["adapter.json"] = """{ "entry": "../../usr/bin/env" }"""
            }, hash: "mx1");

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Installer().InstallAsync("manifest-escape"));
        }

        [TestMethod]
        public async Task An_adapter_needing_a_newer_host_is_refused_with_both_versions()
        {
            await Publish("needs-newer", new Dictionary<string, string>
            {
                ["Adapter.dll"] = "ok",
                ["adapter.json"] = """{ "compatibility": { "minHostVersion": "99.0.0" } }"""
            }, hash: "nn1");

            var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Installer().InstallAsync("needs-newer"));
            StringAssert.Contains(error.Message, "99.0.0");
            StringAssert.Contains(error.Message, HostInfo.Version.ToString());
        }

        [TestMethod]
        public async Task A_runtime_the_host_cannot_launch_is_refused_by_name()
        {
            await Publish("needs-python", new Dictionary<string, string>
            {
                ["main.py"] = "print()",
                ["adapter.json"] = """{ "runtime": "python", "entry": "main.py" }"""
            }, hash: "py1", entryAssembly: "main.py");

            var error = await Assert.ThrowsExceptionAsync<NotSupportedException>(() => Installer().InstallAsync("needs-python"));
            StringAssert.Contains(error.Message, "python");
        }

        [TestMethod]
        public void Host_version_checks_accept_older_and_equal_and_refuse_newer()
        {
            Assert.IsTrue(HostInfo.Satisfies(null));
            Assert.IsTrue(HostInfo.Satisfies("10.0.0"));
            Assert.IsTrue(HostInfo.Satisfies(HostInfo.Baseline));
            Assert.IsFalse(HostInfo.Satisfies("99.0.0"));
            Assert.IsTrue(HostInfo.Satisfies("not a version"), "an unreadable minimum does not block the adapter");
        }

        [TestMethod]
        public void Contained_paths_stay_inside_their_root()
        {
            var root = Path.Combine(Path.GetTempPath(), "root");
            Assert.IsNotNull(AdapterInstaller.ContainedPath(root, "a/b.dll"));
            Assert.IsNull(AdapterInstaller.ContainedPath(root, "../b.dll"));
            Assert.IsNull(AdapterInstaller.ContainedPath(root, "a/../../b.dll"));
            Assert.IsNull(AdapterInstaller.ContainedPath(root, "/etc/passwd"));
            Assert.IsNull(AdapterInstaller.ContainedPath(root, ""));
        }

        [TestMethod]
        public void Plain_hashes_keep_their_name_and_others_map_to_a_stable_digest()
        {
            Assert.AreEqual("abc123-4", AdapterInstaller.DirectoryNameOf("abc123-4"));

            var quoted = AdapterInstaller.DirectoryNameOf("\"abc123\"");
            Assert.AreEqual(quoted, AdapterInstaller.DirectoryNameOf("\"abc123\""));
            Assert.IsTrue(quoted.All(c => char.IsAsciiLetterOrDigit(c)));
            Assert.AreNotEqual(AdapterInstaller.DirectoryNameOf(".."), "..");
        }
    }
}
