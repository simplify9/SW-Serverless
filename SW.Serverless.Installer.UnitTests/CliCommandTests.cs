using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Tooling;

namespace SW.Serverless.Installer.UnitTests;

/// <summary>
/// The commands for adapters in any language, run through the CLI's own entry point as a person or
/// a CI job runs them: init, build, test, run, manifest validate and publish.
/// </summary>
[TestClass]
public class CliCommandTests
{
    static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SW.Serverless.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    static string WorkFolder()
    {
        var folder = Path.Combine(RepositoryRoot(), ".test-work", "cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(Path.Combine(RepositoryRoot(), ".test-work"), true); } catch { }
    }

    /// <summary>Runs the CLI and returns its exit code and what it printed.</summary>
    static async Task<(int Exit, string Output)> Cli(params string[] args)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var exit = await Program.RunAsync(args, _ => null);
            return (exit, output.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [TestMethod]
    public async Task Init_makes_a_project_and_refuses_what_it_can_t_make()
    {
        var work = WorkFolder();

        var (exit, output) = await Cli("init", "AcmeOrders", "--dir", work);
        Assert.AreEqual(Program.Success, exit, output);
        var project = Path.Combine(work, "AcmeOrders");
        foreach (var file in new[] { "AcmeOrders.csproj", "Program.cs", "adapter.json", "settings.example.json", ".gitignore", "README.md" })
            Assert.IsTrue(File.Exists(Path.Combine(project, file)), file);
        Assert.AreEqual("acme.orders", AdapterManifest.Parse(File.ReadAllText(Path.Combine(project, "adapter.json"))).Id);
        StringAssert.Contains(File.ReadAllText(Path.Combine(project, "Program.cs")), "public Task<string> Greet(string name)");

        Assert.AreEqual(Program.Failure, (await Cli("init", "AcmeOrders", "--dir", work)).Exit, "an existing project isn't overwritten");
        var (goExit, goOutput) = await Cli("init", "GoOrders", "--lang", "go", "--dir", work);
        Assert.AreEqual(Program.Failure, goExit);
        StringAssert.Contains(goOutput, "isn't a language the SDK has");
    }

    [TestMethod]
    public async Task Manifest_validate_passes_a_good_file_and_names_what_is_wrong_with_a_bad_one()
    {
        var work = WorkFolder();
        File.WriteAllText(Path.Combine(work, "adapter.json"), """{ "id": "good.adapter", "version": "1.0.0" }""");
        Assert.AreEqual(Program.Success, (await Cli("manifest", "validate", work)).Exit);

        File.WriteAllText(Path.Combine(work, "adapter.json"), """{ "id": "Bad Id", "runtime": "../sh" }""");
        var (exit, output) = await Cli("manifest", "validate", work);
        Assert.AreEqual(Program.Failure, exit);
        StringAssert.Contains(output, "id 'Bad Id'");
        StringAssert.Contains(output, "runtime '../sh'");
    }

    /// <summary>An author's project, as BuildTests makes one: the sample orders processor with an adapter.json.</summary>
    static string AuthorProject()
    {
        var root = RepositoryRoot();
        var project = Path.Combine(WorkFolder(), "AcmeOrders");
        Directory.CreateDirectory(project);
        var sdk = Path.GetRelativePath(project, Path.Combine(root, "SW.Serverless.Sdk", "SW.Serverless.Sdk.csproj"));
        File.WriteAllText(Path.Combine(project, "AcmeOrders.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{sdk}" />
              </ItemGroup>
            </Project>
            """);
        File.Copy(Path.Combine(root, "SW.Serverless.UnitTests.OrdersProcessor", "Program.cs"), Path.Combine(project, "Program.cs"));
        File.WriteAllText(Path.Combine(project, "adapter.json"), """{ "id": "acme.orders", "version": "1.2.0", "displayName": "Acme orders" }""");
        return project;
    }

    [TestMethod]
    public async Task Build_test_run_and_publish_take_a_project_into_storage()
    {
        var project = AuthorProject();
        var settings = Path.Combine(Path.GetDirectoryName(project)!, "settings.json");
        File.WriteAllText(settings, """{ "ApiKey": "test-key" }""");

        var build = await Cli("build", project);
        Assert.AreEqual(Program.Success, build.Exit, build.Output);
        StringAssert.Contains(build.Output, "Source carried");
        var zip = Path.Combine(project, "bin", "serverless", "acme.orders-1.2.0.zip");
        Assert.IsTrue(File.Exists(zip), build.Output);

        // The contract it declares is the application's, handed to the CLI with --contract.
        var contract = Path.Combine(AppContext.BaseDirectory, "Contracts", "orders-adapter-contract.v1.json");
        var test = await Cli("test", zip, "--settings", settings, "--contract", contract);
        Assert.AreEqual(Program.Success, test.Exit, test.Output);
        StringAssert.Contains(test.Output, "PASS orders processor: Process answers example 1");
        StringAssert.Contains(test.Output, "Conforms.");

        var missing = await Cli("test", zip, "--settings", settings, "--contract", Path.Combine(Path.GetDirectoryName(project)!, "nowhere.json"));
        Assert.AreEqual(Program.Failure, missing.Exit);
        StringAssert.Contains(missing.Output, "The contract couldn't be read");

        var run = await Cli("run", zip, "--settings", settings, "--call", "Process", "--input", """{"OrderId":"SO-1"}""");
        Assert.AreEqual(Program.Success, run.Exit, run.Output);
        StringAssert.Contains(run.Output, "\"Accepted\":true");

        var store = Path.Combine(Path.GetDirectoryName(project)!, "store");
        var publish = await Cli("publish", zip, "-p", "local", "-b", "cli-tests", "-u", store);
        Assert.AreEqual(Program.Success, publish.Exit, publish.Output);
        StringAssert.Contains(publish.Output, "Published acme.orders 1.2.0");

        var repository = new AdapterRepository(CloudFilesFactory.Create(new ServerlessUploadOptions
        {
            Provider = "local", BucketName = "cli-tests", ServiceUrl = store,
        }), _ => { });
        var entry = await repository.LoadEntryAsync("acme.orders");
        Assert.AreEqual("1.2.0", entry.Current);
        Assert.IsNotNull(entry.Manifest!.PublishedOn);
        Assert.IsNotNull(entry.Manifest.Source, "it carries its source into storage");

        // Published again with a bump: the version moves on, the old one stays.
        var again = await Cli("publish", zip, "-v", "minor", "-p", "local", "-b", "cli-tests", "-u", store);
        Assert.AreEqual(Program.Success, again.Exit, again.Output);
        Assert.AreEqual("1.3.0", (await repository.LoadEntryAsync("acme.orders")).Current);
    }

    /// <summary>
    /// What init writes, in every language, builds, conforms and answers: a .NET adapter pointed at
    /// this repository's SDK project, and Python and Node ones built with the SDK the CLI carries, so
    /// no NuGet, PyPI or npm is needed.
    /// </summary>
    [DataTestMethod]
    [DataRow("dotnet")]
    [DataRow("python")]
    [DataRow("node")]
    [DataRow("typescript")]
    public async Task What_init_writes_builds_conforms_and_answers(string language)
    {
        if (language == "typescript" && !NodeStripsTypes())
            Assert.Inconclusive("this machine's node can't strip TypeScript types; it takes Node 22.13 or later");

        var root = RepositoryRoot();
        var work = WorkFolder();
        var name = "Greeter" + char.ToUpper(language[0]) + language[1..];
        Assert.AreEqual(Program.Success, (await Cli("init", name, "--lang", language, "--dir", work)).Exit);
        var project = Path.Combine(work, name);

        if (language == "dotnet")
        {
            var csproj = Directory.GetFiles(project, "*.csproj").Single();
            File.WriteAllText(csproj, File.ReadAllText(csproj)
                .Replace($"<PackageReference Include=\"SimplyWorks.Serverless.Sdk\" Version=\"{Tooling.Scaffolding.Scaffolder.SdkPackageVersion}\" />",
                    $"<ProjectReference Include=\"{Path.Combine(root, "SW.Serverless.Sdk", "SW.Serverless.Sdk.csproj")}\" />")
                .Replace("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", ""));
        }

        var build = await Cli("build", project);
        Assert.AreEqual(Program.Success, build.Exit, build.Output);

        var package = Path.Combine(project, "bin", "serverless", "package");
        var manifest = AdapterManifest.Parse(File.ReadAllText(Path.Combine(package, "adapter.json")));
        Assert.AreEqual(Tooling.Scaffolding.Scaffolder.IdFrom(name), manifest.Id);
        Assert.AreEqual(language switch { "python" => "python", "node" or "typescript" => "node", _ => "dotnet" }, manifest.Runtime);
        CollectionAssert.AreEquivalent(new[] { "Greeting", "ApiKey" }, manifest.Properties.Select(p => p.Name).ToArray());
        Assert.IsTrue(manifest.Properties.Single(p => p.Name == "ApiKey").Secret);
        Assert.IsNull(manifest.Contracts, "the generic adapter implements no contract");
        Assert.IsTrue(manifest.Source.Files.Count > 0, "it carries its source");
        if (language == "python")
            Assert.IsTrue(File.Exists(Path.Combine(package, "_vendor", "sw_serverless", "__init__.py")));
        if (language is "node" or "typescript")
            Assert.IsTrue(File.Exists(Path.Combine(package, "node_modules", "@simplyworks", "sw-serverless", "src", "index.js")));

        var test = await Cli("test", project);
        Assert.AreEqual(Program.Success, test.Exit, test.Output);

        var run = await Cli("run", project, "--call", "Greet", "--input", "Ada");
        Assert.AreEqual(Program.Success, run.Exit, run.Output);
        StringAssert.Contains(run.Output, "Hello, Ada!");
    }

    static bool NodeStripsTypes()
    {
        try
        {
            using var node = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node",
                "-e \"process.exit(typeof require('node:module').stripTypeScriptTypes === 'function' ? 0 : 1)\"")
                { RedirectStandardOutput = true, RedirectStandardError = true });
            node!.WaitForExit();
            return node.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
