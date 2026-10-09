using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Runtimes;
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

        static AdapterInstaller Installer(AdapterRuntimeOptions runtimes = null) => new(
            services.GetRequiredService<ServerlessOptions>(),
            new MemoryCache(new MemoryCacheOptions()),
            cloudFiles,
            new AdapterRuntimes(runtimes));

        /// <summary>A host whose interpreters are at paths that don't exist, whatever this machine has.</summary>
        static AdapterRuntimeOptions NoInterpreters => new()
        {
            PythonExecutable = "/nonexistent/python3",
            NodeExecutable = "/nonexistent/node",
        };

        /// <summary>A host whose "python" answers with a fixed version, so version checks don't depend on this machine.</summary>
        static AdapterRuntimeOptions PythonReporting(string version)
        {
            var script = Path.Combine(Path.GetTempPath(), $"fake-python-{Guid.NewGuid():N}.sh");
            File.WriteAllText(script, $"#!/bin/sh\necho \"Python {version}\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new AdapterRuntimeOptions { PythonExecutable = script };
        }

        static async Task Publish(string adapterId, IDictionary<string, string> entries,
            string hash, string entryAssembly = "Adapter.dll", string key = null)
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
                Key = key ?? $"adapters/{adapterId}",
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
            await Publish("needs-ruby", new Dictionary<string, string>
            {
                ["main.rb"] = "puts",
                ["adapter.json"] = """{ "runtime": "ruby", "entry": "main.rb" }"""
            }, hash: "rb1", entryAssembly: "main.rb");

            var error = await Assert.ThrowsExceptionAsync<NotSupportedException>(() => Installer().InstallAsync("needs-ruby"));
            StringAssert.Contains(error.Message, "'ruby'");
        }

        [TestMethod]
        public async Task A_known_runtime_missing_from_the_host_is_refused_saying_so()
        {
            await Publish("needs-python", new Dictionary<string, string>
            {
                ["main.py"] = "print()",
                ["adapter.json"] = """{ "runtime": "python", "entry": "main.py" }"""
            }, hash: "py1", entryAssembly: "main.py");

            var error = await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
                Installer(NoInterpreters).InstallAsync("needs-python"));
            StringAssert.Contains(error.Message, "'python'");
            StringAssert.Contains(error.Message, "not installed");
        }

        [TestMethod]
        public async Task A_runtime_version_outside_the_manifest_s_range_is_refused_and_one_inside_installs()
        {
            await Publish("needs-python312", new Dictionary<string, string>
            {
                ["main.py"] = "print()",
                ["adapter.json"] = """{ "runtime": "python", "runtimeVersion": ">=3.12", "entry": "main.py" }"""
            }, hash: "py312", entryAssembly: "main.py");

            var error = await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
                Installer(PythonReporting("3.11.9")).InstallAsync("needs-python312"));
            StringAssert.Contains(error.Message, ">=3.12");
            StringAssert.Contains(error.Message, "3.11.9");

            var installed = await Installer(PythonReporting("3.12.4")).InstallAsync("needs-python312");
            Assert.AreEqual("python", installed.Runtime);
            Assert.IsTrue(installed.LocalPath.EndsWith("main.py"));
        }

        [TestMethod]
        public async Task A_package_for_other_platforms_is_refused_and_one_for_this_platform_runs_its_own_entry()
        {
            await Publish("elsewhere", new Dictionary<string, string>
            {
                ["adapter"] = "binary",
                ["adapter.json"] = """{ "runtime": "exec", "entry": "adapter", "platforms": ["plan9-mips"] }"""
            }, hash: "ex1", entryAssembly: "adapter");
            var error = await Assert.ThrowsExceptionAsync<NotSupportedException>(() => Installer().InstallAsync("elsewhere"));
            StringAssert.Contains(error.Message, "plan9-mips");

            var here = AdapterRuntimes.CurrentPlatform;
            await Publish("everywhere", new Dictionary<string, string>
            {
                ["default/adapter"] = "default build",
                [$"{here}/adapter"] = "this platform's build",
                ["adapter.json"] = $$"""{ "runtime": "exec", "entry": "default/adapter", "platforms": ["{{here}}", "plan9-mips"], "entries": { "{{here}}": "{{here}}/adapter" } }"""
            }, hash: "ex2", entryAssembly: "default/adapter");

            var installed = await Installer().InstallAsync("everywhere");
            Assert.AreEqual("this platform's build", File.ReadAllText(installed.LocalPath));
            if (!OperatingSystem.IsWindows())
                Assert.IsTrue(File.GetUnixFileMode(installed.LocalPath).HasFlag(UnixFileMode.UserExecute),
                    "an exec entry must be executable after unpacking");
        }

        [TestMethod]
        public async Task A_dotnet_adapter_installs_without_asking_whether_dotnet_is_there()
        {
            // As before runtimes: production images carry the runtime without the SDK, and a
            // .NET adapter was never refused for want of a check.
            await Publish("plain-dotnet", new Dictionary<string, string>
            {
                ["Adapter.dll"] = "assembly",
                ["adapter.json"] = """{ "runtime": "dotnet", "entry": "Adapter.dll" }"""
            }, hash: "dn1");

            var installed = await Installer(new AdapterRuntimeOptions { DotnetExecutable = "/nonexistent/dotnet" })
                .InstallAsync("plain-dotnet");
            Assert.AreEqual("dotnet", installed.Runtime);
        }

        [TestMethod]
        public async Task A_pinned_version_is_read_from_the_versions_prefix()
        {
            await Publish("pinned", new Dictionary<string, string> { ["Adapter.dll"] = "current" }, hash: "pc1");
            await Publish("pinned", new Dictionary<string, string> { ["Adapter.dll"] = "one-oh" }, hash: "p100",
                key: "adapters-versions/pinned/1.0.0");

            var current = await Installer().InstallAsync("pinned");
            var pinned = await Installer().InstallAsync("pinned/1.0.0");

            Assert.AreEqual("current", File.ReadAllText(current.LocalPath));
            Assert.AreEqual("one-oh", File.ReadAllText(pinned.LocalPath));
            Assert.AreEqual("adapters-versions/pinned/1.0.0", pinned.RemoteKey);
        }

        [TestMethod]
        public async Task A_version_an_older_installer_published_is_still_found()
        {
            // Where the installer put versions before they had a prefix of their own.
            await Publish("legacy-pinned", new Dictionary<string, string> { ["Adapter.dll"] = "old-layout" }, hash: "lp1",
                key: "adapters/legacy-pinned/2.0.0");

            var pinned = await Installer().InstallAsync("legacy-pinned/2.0.0");

            Assert.AreEqual("old-layout", File.ReadAllText(pinned.LocalPath));
            Assert.AreEqual("adapters/legacy-pinned/2.0.0", pinned.RemoteKey);
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
