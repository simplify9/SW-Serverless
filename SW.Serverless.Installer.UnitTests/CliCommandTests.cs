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

        var (exit, output) = await Cli("init", "AcmeOrders", "--kind", "validator", "--dir", work);
        Assert.AreEqual(Program.Success, exit, output);
        var project = Path.Combine(work, "AcmeOrders");
        foreach (var file in new[] { "AcmeOrders.csproj", "Program.cs", "adapter.json", "settings.example.json", ".gitignore", "README.md" })
            Assert.IsTrue(File.Exists(Path.Combine(project, file)), file);
        Assert.AreEqual("acme.orders", AdapterManifest.Parse(File.ReadAllText(Path.Combine(project, "adapter.json"))).Id);
        StringAssert.Contains(File.ReadAllText(Path.Combine(project, "Program.cs")), "IBitweenValidator");

        Assert.AreEqual(Program.Failure, (await Cli("init", "AcmeOrders", "--dir", work)).Exit, "an existing project isn't overwritten");
        var (nodeExit, nodeOutput) = await Cli("init", "NodeOrders", "--lang", "node", "--dir", work);
        Assert.AreEqual(Program.Failure, nodeExit);
        StringAssert.Contains(nodeOutput, "arrives with that language's SDK");
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

    /// <summary>An author's project, as BuildTests makes one: the BitweenHandler adapter with an adapter.json.</summary>
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
        File.Copy(Path.Combine(root, "SW.Serverless.UnitTests.BitweenHandler", "Program.cs"), Path.Combine(project, "Program.cs"));
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

        var test = await Cli("test", zip, "--settings", settings);
        Assert.AreEqual(Program.Success, test.Exit, test.Output);
        StringAssert.Contains(test.Output, "PASS bitween handler: Handle answers example 1");
        StringAssert.Contains(test.Output, "Conforms.");

        var run = await Cli("run", zip, "--settings", settings, "--call", "Handle", "--input", """{"Data":"{\"order\":1}"}""");
        Assert.AreEqual(Program.Success, run.Exit, run.Output);
        StringAssert.Contains(run.Output, "accepted");

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
    /// What init writes builds and conforms. The templates reference the published SDK and Bitween
    /// contract packages; until those are on NuGet the project is pointed at the projects themselves,
    /// with Bitween-api beside this repository.
    /// </summary>
    [DataTestMethod]
    [DataRow("handler")]
    [DataRow("receiver")]
    [DataRow("validator")]
    public async Task What_init_writes_builds_and_conforms(string kind)
    {
        var root = RepositoryRoot();
        var contracts = Path.GetFullPath(Path.Combine(root, "..", "Bitween-api", "SW.Bitween.Adapters", "SW.Bitween.Adapters.csproj"));
        if (!File.Exists(contracts)) Assert.Inconclusive($"Bitween-api isn't beside this repository ({contracts})");

        var work = WorkFolder();
        Assert.AreEqual(Program.Success, (await Cli("init", "Acme" + char.ToUpper(kind[0]) + kind[1..], "--kind", kind, "--dir", work)).Exit);
        var project = Directory.GetDirectories(work).Single();
        var csproj = Directory.GetFiles(project, "*.csproj").Single();
        var text = File.ReadAllText(csproj)
            .Replace($"<PackageReference Include=\"SimplyWorks.Serverless.Sdk\" Version=\"{Tooling.Scaffolding.Scaffolder.SdkPackageVersion}\" />",
                $"<ProjectReference Include=\"{Path.Combine(root, "SW.Serverless.Sdk", "SW.Serverless.Sdk.csproj")}\" />")
            .Replace($"<PackageReference Include=\"SimplyWorks.Bitween.Adapters\" Version=\"{Tooling.Scaffolding.Scaffolder.BitweenAdaptersPackageVersion}\" />",
                $"<ProjectReference Include=\"{contracts}\" />")
            .Replace("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", "");
        File.WriteAllText(csproj, text);

        var build = await Cli("build", project, "--no-source");
        Assert.AreEqual(Program.Success, build.Exit, build.Output);

        var settings = Path.Combine(work, "settings.json");
        File.WriteAllText(settings, """{ "ApiKey": "k" }""");
        var test = await Cli("test", Path.Combine(project, "bin", "serverless", "package"), "--settings", settings);
        Assert.AreEqual(Program.Success, test.Exit, test.Output);
    }

    /// <summary>
    /// A Python adapter, from init to a package that conforms: built with the SDK and the Bitween
    /// kinds vendored from the copies the CLI carries, so no PyPI and no network are needed.
    /// </summary>
    [DataTestMethod]
    [DataRow("handler")]
    [DataRow("mapper")]
    [DataRow("receiver")]
    [DataRow("validator")]
    public async Task What_init_writes_in_python_builds_and_conforms(string kind)
    {
        var work = WorkFolder();
        var name = "Py" + char.ToUpper(kind[0]) + kind[1..];
        Assert.AreEqual(Program.Success, (await Cli("init", name, "--lang", "python", "--kind", kind, "--dir", work)).Exit);
        var project = Path.Combine(work, name);
        Assert.IsTrue(File.Exists(Path.Combine(project, "main.py")));

        var build = await Cli("build", project);
        Assert.AreEqual(Program.Success, build.Exit, build.Output);

        var package = Path.Combine(project, "bin", "serverless", "package");
        var manifest = SW.Serverless.Contract.Catalog.AdapterManifest.Parse(File.ReadAllText(Path.Combine(package, "adapter.json")));
        Assert.AreEqual("python", manifest.Runtime);
        Assert.AreEqual(Tooling.Building.PythonBuild.EntryScript, manifest.Entry);
        Assert.AreEqual("classic", manifest.Lifecycle);
        Assert.AreEqual(2, manifest.Protocol.Min);
        CollectionAssert.AreEqual(new[] { kind }, manifest.Kinds);
        Assert.AreEqual(1, manifest.Contracts["bitween"]);
        Assert.IsNull(manifest.Platforms, "nothing native: it runs anywhere");
        Assert.IsTrue(File.Exists(Path.Combine(package, "_vendor", "simplyworks_serverless", "__init__.py")));
        Assert.IsTrue(File.Exists(Path.Combine(package, "_vendor", "simplyworks_bitween", "__init__.py")));
        Assert.IsTrue(manifest.Source.Files.ContainsKey("main.py"));
        Assert.IsTrue(File.Exists(Path.Combine(package, "source", "main.py")));

        var settings = Path.Combine(work, "settings.json");
        File.WriteAllText(settings, """{ "ApiKey": "k" }""");
        // The project folder: built first, then checked.
        var test = await Cli("test", project, "--settings", settings);
        Assert.AreEqual(Program.Success, test.Exit, test.Output);
    }

    /// <summary>
    /// The CLI vendors its own copy of the Bitween kinds for Python, as it carries its own copy of
    /// the contract; it must be the one Bitween-api maintains.
    /// </summary>
    [TestMethod]
    public void The_python_bitween_kinds_the_cli_carries_are_bitween_s()
    {
        var root = RepositoryRoot();
        var original = Path.GetFullPath(Path.Combine(root, "..", "Bitween-api", "sdk", "python", "src", "simplyworks_bitween", "__init__.py"));
        if (!File.Exists(original)) Assert.Inconclusive($"Bitween-api isn't beside this repository ({original})");
        var copy = Path.Combine(root, "SW.Serverless.Tooling", "Contracts", "bitween", "python", "simplyworks_bitween", "__init__.py");
        Assert.AreEqual(File.ReadAllText(original), File.ReadAllText(copy),
            "copy Bitween-api/sdk/python/src/simplyworks_bitween into SW.Serverless.Tooling/Contracts/bitween/python");
    }
}
