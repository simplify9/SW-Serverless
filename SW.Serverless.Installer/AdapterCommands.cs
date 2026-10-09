using CommandLine;
using SW.PrimitiveTypes;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Tooling;
using SW.Serverless.Tooling.Building;
using SW.Serverless.Tooling.Conformance;
using SW.Serverless.Tooling.Scaffolding;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SW.Serverless.Installer
{
    /// <summary><c>sw-serverless init &lt;name&gt;</c></summary>
    public class InitCliOptions
    {
        [Value(0, Required = true, MetaName = "name", HelpText = "The adapter's name, e.g. AcmeOrders: its folder, project and class.")]
        public string Name { get; set; }

        [Option("lang", Default = "dotnet", HelpText = "Its language: dotnet, python, node (JavaScript) or typescript.")]
        public string Language { get; set; }

        [Option("id", HelpText = "Its id in storage; derived from the name when not given (AcmeOrders -> acme.orders).")]
        public string Id { get; set; }

        [Option("dir", Default = ".", HelpText = "The folder to make it in.")]
        public string Directory { get; set; }
    }

    /// <summary><c>sw-serverless build [project]</c></summary>
    public class BuildCliOptions
    {
        [Value(0, MetaName = "project", Default = ".", HelpText = "The adapter's project folder, with its adapter.json.")]
        public string Project { get; set; }

        [Option('o', "out", HelpText = "Where the package goes; bin/serverless in the project unless set.")]
        public string Output { get; set; }

        [Option("no-source", HelpText = "Leave the source out of the package.")]
        public bool NoSource { get; set; }

        [Option("allow", HelpText = "Source files whose secret-scan findings are false positives, separated by spaces.")]
        public IEnumerable<string> Allow { get; set; }

        [Option("dry-run", HelpText = "Show the source that would be carried, and build nothing.")]
        public bool DryRun { get; set; }
    }

    /// <summary><c>sw-serverless test [package]</c></summary>
    public class TestCliOptions
    {
        [Value(0, MetaName = "package", Default = ".",
            HelpText = "A package zip, a package folder, or a project folder (built first).")]
        public string Package { get; set; }

        [Option("settings", HelpText = "A JSON file of settings to run it with: { \"Name\": \"value\" }. The adapter calls whatever they point at.")]
        public string Settings { get; set; }

        [Option("allow-delete", HelpText = "Let a receiver's DeleteFile run: it removes or moves a real file at the source.")]
        public bool AllowDelete { get; set; }

        [Option("contract", HelpText = "Contract files to check against, separated by spaces.")]
        public IEnumerable<string> Contracts { get; set; }

        [Option("timeout", Default = 60, HelpText = "Seconds one call may take.")]
        public int Timeout { get; set; }
    }

    /// <summary><c>sw-serverless run [package] --call Command</c></summary>
    public class RunCliOptions
    {
        [Value(0, MetaName = "package", Default = ".", HelpText = "A package zip, a package folder, or a project folder (built first).")]
        public string Package { get; set; }

        [Option("settings", HelpText = "A JSON file of settings to run it with.")]
        public string Settings { get; set; }

        [Option("call", Required = true, HelpText = "The command to call, e.g. Handle.")]
        public string Command { get; set; }

        [Option("input", HelpText = "Its argument: JSON, plain text, or @file to read it from a file.")]
        public string Input { get; set; }

        [Option("timeout", Default = 60, HelpText = "Seconds the call may take.")]
        public int Timeout { get; set; }
    }

    /// <summary><c>sw-serverless manifest validate [path]</c></summary>
    public class ManifestCliOptions
    {
        [Value(0, Required = true, MetaName = "action", HelpText = "validate")]
        public string Action { get; set; }

        [Value(1, MetaName = "path", Default = ".", HelpText = "An adapter.json, or the folder holding one.")]
        public string Path { get; set; }
    }

    /// <summary><c>sw-serverless publish &lt;package&gt;</c></summary>
    public class PublishPackageCliOptions : StorageCliOptions
    {
        [Value(0, Required = true, MetaName = "package", HelpText = "A package zip from sw-serverless build.")]
        public string Package { get; set; }

        [Option('v', "version", HelpText = "The version: explicit, or major, minor or patch to bump. The manifest's version unless set.")]
        public string Version { get; set; }

        [Option("no-promote", HelpText = "Upload the version without making it the one that runs.")]
        public bool NoPromote { get; set; }

        [Option("notes", HelpText = "Release notes for this version (Markdown).")]
        public string Notes { get; set; }

        [Option("published-by", HelpText = "Who is publishing, for the catalog.")]
        public string PublishedBy { get; set; }
    }

    /// <summary>The commands that work on adapters in any language, over SW.Serverless.Tooling.</summary>
    static class AdapterCommands
    {
        const int Success = Program.Success;
        const int Failure = Program.Failure;

        public static Task<int> Init(InitCliOptions opts)
        {
            var result = Scaffolder.Scaffold(new ScaffoldRequest
            {
                Name = opts.Name,
                Id = opts.Id,
                Language = opts.Language,
                ParentDirectory = opts.Directory,
            });
            if (!result.Succeeded)
            {
                foreach (var problem in result.Problems) Console.WriteLine(problem);
                return Task.FromResult(Failure);
            }

            Console.WriteLine($"Made an adapter in {result.ProjectDirectory}:");
            foreach (var file in result.Files) Console.WriteLine($"  {file}");
            Console.WriteLine("Next: sw-serverless build, then sw-serverless test --settings settings.json");
            return Task.FromResult(Success);
        }

        public static async Task<int> Build(BuildCliOptions opts)
        {
            var result = await PackageBuilder.BuildAsync(new BuildRequest
            {
                ProjectDirectory = opts.Project,
                OutputDirectory = opts.Output,
                IncludeSource = !opts.NoSource,
                AllowedFiles = new HashSet<string>(opts.Allow ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase),
                DryRun = opts.DryRun,
                Log = Console.WriteLine,
            });

            if (result.SourceFiles.Count > 0)
            {
                Console.WriteLine($"Source carried ({result.SourceFiles.Count} files, {result.SourceFiles.Sum(f => f.Bytes) / 1024} KB):");
                foreach (var (path, bytes) in result.SourceFiles.OrderBy(f => f.Path, StringComparer.Ordinal))
                    Console.WriteLine($"  {path} ({bytes} B)");
            }
            foreach (var warning in result.Warnings) Console.WriteLine($"Warning: {warning}");
            foreach (var problem in result.Problems) Console.WriteLine(problem);
            if (!result.Succeeded) return Failure;

            if (!opts.DryRun) Console.WriteLine($"Built {result.ZipPath}");
            return Success;
        }

        public static async Task<int> Test(TestCliOptions opts)
        {
            List<ContractDocument> contracts;
            try
            {
                contracts = (opts.Contracts ?? Enumerable.Empty<string>()).Select(ContractDocument.FromFile).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
            {
                Console.WriteLine($"The contract couldn't be read: {ex.Message}");
                return Failure;
            }

            var (package, cleanup) = await PackageFolderAsync(opts.Package);
            if (package == null) return Failure;
            try
            {
                var report = await new ConformanceRunner().RunAsync(new ConformanceOptions
                {
                    PackageDirectory = package,
                    Settings = ReadSettings(opts.Settings),
                    AllowDelete = opts.AllowDelete,
                    Contracts = contracts,
                    CommandTimeoutSeconds = opts.Timeout,
                    Log = Console.WriteLine,
                });

                foreach (var check in report.Checks)
                    Console.WriteLine($"{Mark(check.Outcome)} {check.Name}{(string.IsNullOrEmpty(check.Detail) ? "" : " — " + check.Detail)}");
                var failed = report.Checks.Count(c => c.Outcome == CheckOutcome.Failed);
                Console.WriteLine(failed == 0 ? "Conforms." : $"{failed} check{(failed == 1 ? "" : "s")} failed.");
                return report.Passed ? Success : Failure;
            }
            finally
            {
                cleanup();
            }
        }

        public static async Task<int> Run(RunCliOptions opts)
        {
            var (package, cleanup) = await PackageFolderAsync(opts.Package);
            if (package == null) return Failure;
            try
            {
                await using var host = await LocalAdapterHost.StartAsync(package, ReadSettings(opts.Settings), commandTimeoutSeconds: opts.Timeout);
                Console.WriteLine(await host.CallAsync(opts.Command, ReadInput(opts.Input)));
                return Success;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.WriteLine(ex.GetBaseException().Message);
                return Failure;
            }
            finally
            {
                cleanup();
            }
        }

        public static Task<int> Manifest(ManifestCliOptions opts)
        {
            if (opts.Action != "validate")
            {
                Console.WriteLine($"'{opts.Action}' isn't a manifest action: use validate");
                return Task.FromResult(Failure);
            }

            var path = Directory.Exists(opts.Path) ? System.IO.Path.Combine(opts.Path, AdapterManifest.FileName) : opts.Path;
            if (!File.Exists(path))
            {
                Console.WriteLine($"There is no {path}");
                return Task.FromResult(Failure);
            }

            try
            {
                var problems = AdapterManifest.Parse(File.ReadAllText(path)).Validate();
                foreach (var problem in problems) Console.WriteLine(problem);
                Console.WriteLine(problems.Count == 0 ? $"{path} is valid." : $"{problems.Count} problem{(problems.Count == 1 ? "" : "s")} in {path}.");
                return Task.FromResult(problems.Count == 0 ? Success : Failure);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"{path} isn't valid JSON: {ex.Message}");
                return Task.FromResult(Failure);
            }
        }

        public static Task<int> Publish(PublishPackageCliOptions opts, Func<string, string> environment) =>
            Program.WithStorage(opts, environment, async files =>
            {
                var result = await PackagePublisher.PublishPackageAsync(files, new PublishPackageRequest
                {
                    PackagePath = opts.Package,
                    Version = opts.Version,
                    Promote = !opts.NoPromote,
                    ReleaseNotes = opts.Notes,
                    PublishedBy = UploadOptionsResolver.ResolvePublishedBy(opts.PublishedBy, environment),
                }, Console.WriteLine);
                Console.WriteLine($"Published {result.Manifest.Id} {result.Version}.");
            });

        static string Mark(CheckOutcome outcome) => outcome switch
        {
            CheckOutcome.Passed => "PASS",
            CheckOutcome.Failed => "FAIL",
            _ => "SKIP",
        };

        /// <summary>A package folder for whatever was given: a folder with adapter.json, a zip, or a project to build.</summary>
        static async Task<(string Folder, Action Cleanup)> PackageFolderAsync(string given)
        {
            var path = System.IO.Path.GetFullPath(given);
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swsl-cli", Guid.NewGuid().ToString("N"));
                ZipFile.ExtractToDirectory(path, folder);
                return (folder, () => TryDelete(folder));
            }

            if (Directory.Exists(path) && (Directory.GetFiles(path, "*.*proj").Length > 0 || IsUnbuiltScript(path)))
            {
                var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swsl-cli", Guid.NewGuid().ToString("N"));
                var built = await PackageBuilder.BuildAsync(new BuildRequest { ProjectDirectory = path, OutputDirectory = output, Log = Console.WriteLine });
                foreach (var problem in built.Problems) Console.WriteLine(problem);
                return built.Succeeded ? (built.PackageDirectory, () => TryDelete(output)) : (null, () => TryDelete(output));
            }

            if (Directory.Exists(path) && File.Exists(System.IO.Path.Combine(path, AdapterManifest.FileName)))
                return (path, () => { });

            Console.WriteLine($"{given} is neither a package zip, a package folder nor a project folder");
            return (null, () => { });
        }

        /// <summary>
        /// A Python or Node project rather than a built package: its manifest names the runtime, and
        /// nothing a build writes is there — the Python entry, or the SDK in node_modules.
        /// </summary>
        static bool IsUnbuiltScript(string folder)
        {
            var manifestPath = System.IO.Path.Combine(folder, AdapterManifest.FileName);
            if (!File.Exists(manifestPath)) return false;
            try
            {
                var runtime = AdapterManifest.Parse(File.ReadAllText(manifestPath)).Runtime;
                if (string.Equals(runtime, AdapterManifest.PythonRuntime, StringComparison.OrdinalIgnoreCase))
                    return !File.Exists(System.IO.Path.Combine(folder, PythonBuild.EntryScript));
                if (string.Equals(runtime, AdapterManifest.NodeRuntime, StringComparison.OrdinalIgnoreCase))
                    return !File.Exists(System.IO.Path.Combine(folder, "node_modules", "@simplyworks", "sw-serverless", "package.json"));
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        static IDictionary<string, string> ReadSettings(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return new Dictionary<string, string>();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.EnumerateObject().ToDictionary(p => p.Name,
                p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText());
        }

        static object ReadInput(string input)
        {
            if (input == null) return null;
            if (input.StartsWith('@')) input = File.ReadAllText(input[1..]);
            // JSON goes as JSON; anything else as the plain text a string argument is.
            try { return Newtonsoft.Json.Linq.JToken.Parse(input); }
            catch (Newtonsoft.Json.JsonReaderException) { return input; }
        }

        static void TryDelete(string folder)
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
