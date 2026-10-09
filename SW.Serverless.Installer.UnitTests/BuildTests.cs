using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Tooling.Building;
using SW.Serverless.Tooling.Conformance;

namespace SW.Serverless.Installer.UnitTests;

[TestClass]
public class IgnoreRuleTests
{
    [DataTestMethod]
    [DataRow("bin/Debug/net10.0/a.dll", true)]
    [DataRow("src/obj/project.assets.json", true)]
    [DataRow("node_modules/x/index.js", true)]
    [DataRow(".env", true)]
    [DataRow("config/.env.production", true)]
    [DataRow("keys/partner.pem", true)]
    [DataRow("appsettings.Production.json", true)]
    [DataRow("appsettings.json", false)]
    [DataRow("Handler.cs", false)]
    [DataRow("src/binder/Thing.cs", false)]
    public void Built_in_rules(string path, bool ignored) =>
        Assert.AreEqual(ignored, new IgnoreRules(IgnoreRules.BuiltIn).Ignores(path), path);

    [TestMethod]
    public void A_project_s_own_patterns_add_to_them_and_can_bring_a_file_back()
    {
        var rules = new IgnoreRules(IgnoreRules.BuiltIn.Concat(new[] { "fixtures/**/*.zip", "/local.txt", "*.log", "!keep.log", "!keys/public.pem" }));

        Assert.IsTrue(rules.Ignores("fixtures/a/b/big.zip"));
        Assert.IsTrue(rules.Ignores("fixtures/big.zip"));
        Assert.IsTrue(rules.Ignores("local.txt"));
        Assert.IsFalse(rules.Ignores("sub/local.txt"), "a leading slash anchors to the root");
        Assert.IsTrue(rules.Ignores("logs/run.log"));
        Assert.IsFalse(rules.Ignores("keep.log"));
        Assert.IsFalse(rules.Ignores("keys/public.pem"));
    }
}

[TestClass]
public class SecretScannerTests
{
    [DataTestMethod]
    [DataRow("-----BEGIN RSA PRIVATE KEY-----", "a private key")]
    [DataRow("-----BEGIN OPENSSH PRIVATE KEY-----", "a private key")]
    [DataRow("var key = \"AKIAIOSFODNN7EXAMPLE\";", "an AWS access key")]
    [DataRow("\"Db\": \"Server=x;User Id=sa;Password=Sup3rS3cret!;\"", "a password in a connection string")]
    [DataRow("const apiKey = \"9f8e7d6c5b4a3f2e1d0c\";", "a secret assigned in code")]
    [DataRow("client_secret: 'abcdefgh12345678'", "a secret assigned in code")]
    [DataRow("token = ghp_abcdefghijklmnopqrstuvwxyz0123456789AB", "a GitHub token")]
    public void A_secret_is_found_and_named(string line, string kind)
    {
        var finding = SecretScanner.Scan("Handler.cs", "first line\n" + line + "\n").Single();
        Assert.AreEqual(2, finding.Line);
        Assert.AreEqual(kind, finding.Kind);
    }

    [DataTestMethod]
    [DataRow("Runner.Expect(\"Password\", isPrivate: true);")]
    [DataRow("var password = Runner.StartupValueOf(\"Password\");")]
    [DataRow("\"Password\": \"********\"")]
    [DataRow("apiKey = \"your-api-key-here\"")]
    [DataRow("secret: \"${SECRET}\"")]
    [DataRow("Password={password};")]
    public void Code_that_only_names_or_reads_a_secret_is_not_reported(string line) =>
        Assert.AreEqual(0, SecretScanner.Scan("Handler.cs", line).Count(), line);
}

/// <summary>
/// serverless build on a real adapter project: built, described, its manifest written from the
/// adapter itself, its source carried under the rules — and the package it makes is what the
/// conformance kit and any host run.
/// </summary>
[TestClass]
public class BuildTests
{
    static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SW.Serverless.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>
    /// A copy of the OrdersProcessor test adapter as an author's project: inside the repository, so
    /// its reference to the SDK project still resolves, with an adapter.json and a few files the
    /// source rules must leave out.
    /// </summary>
    static string AuthorProject(string authorJson = null, IDictionary<string, string> extraFiles = null)
    {
        var root = RepositoryRoot();
        var project = Path.Combine(root, ".test-work", Guid.NewGuid().ToString("N"), "AcmeOrders");
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
        File.WriteAllText(Path.Combine(project, AdapterManifest.FileName), authorJson ??
            """{ "id": "acme.orders", "version": "1.2.0", "displayName": "Acme orders", "properties": [ { "name": "Mode", "type": "select", "options": ["working", "broken"] } ] }""");
        File.WriteAllText(Path.Combine(project, ".env"), "API_KEY=never-carried");
        File.WriteAllText(Path.Combine(project, "notes.log"), "left out by .serverlessignore");
        File.WriteAllText(Path.Combine(project, ".serverlessignore"), "*.log\n");
        foreach (var (name, content) in extraFiles ?? new Dictionary<string, string>())
            File.WriteAllText(Path.Combine(project, name), content);
        return project;
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(Path.Combine(RepositoryRoot(), ".test-work"), true); } catch { }
    }

    static Task<BuildResult> Build(string project, Action<BuildRequest> configure = null)
    {
        var request = new BuildRequest { ProjectDirectory = project, Log = Console.WriteLine };
        configure?.Invoke(request);
        return PackageBuilder.BuildAsync(request);
    }

    [TestMethod]
    public async Task A_build_writes_the_manifest_from_the_adapter_and_carries_its_source_under_the_rules()
    {
        var project = AuthorProject();
        var result = await Build(project);
        Assert.IsTrue(result.Succeeded, string.Join("; ", result.Problems));

        var manifest = result.Manifest;
        Assert.AreEqual("acme.orders", manifest.Id);
        Assert.AreEqual("1.2.0", manifest.Version);
        Assert.AreEqual("Acme orders", manifest.DisplayName, "the author's text is kept");
        Assert.AreEqual("dotnet", manifest.Runtime);
        Assert.AreEqual("AcmeOrders.dll", manifest.Entry);
        Assert.AreEqual("10.1.0", manifest.SdkVersion);
        Assert.AreEqual(AdapterManifest.ClassicLifecycle, manifest.Lifecycle);
        CollectionAssert.AreEqual(new[] { "processor" }, manifest.Kinds);
        Assert.AreEqual(1, manifest.Contracts!["orders"]);

        var mode = manifest.Properties.Single(p => p.Name == "Mode");
        Assert.AreEqual("select", mode.Type, "the author's presentation is kept");
        Assert.AreEqual("working", mode.Default, "the adapter's default wins");
        var apiKey = manifest.Properties.Single(p => p.Name == "ApiKey");
        Assert.IsTrue(apiKey.Required);
        Assert.IsTrue(apiKey.Secret);
        Assert.IsNull(apiKey.Default);

        var carried = manifest.Source!.Files.Keys.ToList();
        Assert.IsTrue(carried.Any(f => f.EndsWith("AcmeOrders/Program.cs")), string.Join(", ", carried));
        Assert.IsTrue(carried.Any(f => f.EndsWith("AcmeOrders/AcmeOrders.csproj")));
        Assert.IsTrue(carried.Any(f => f.EndsWith("SW.Serverless.Sdk/Runner.cs")), "the local project it references, so it rebuilds");
        Assert.IsFalse(carried.Any(f => f.EndsWith(".env") || f.EndsWith("notes.log") || f.Contains("/bin/") || f.Contains("/obj/")),
            string.Join(", ", carried.Where(f => f.EndsWith(".env") || f.EndsWith(".log") || f.Contains("/bin/") || f.Contains("/obj/"))));

        using var zip = ZipFile.OpenRead(result.ZipPath);
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        CollectionAssert.Contains(names, "adapter.json");
        CollectionAssert.Contains(names, "AcmeOrders.dll");
        Assert.IsTrue(names.Any(n => n.StartsWith("source/") && n.EndsWith("AcmeOrders/Program.cs")));
        Assert.IsTrue(result.ZipPath.EndsWith("acme.orders-1.2.0.zip"));
    }

    [TestMethod]
    public async Task What_a_build_makes_passes_the_conformance_kit()
    {
        var result = await Build(AuthorProject());
        Assert.IsTrue(result.Succeeded, string.Join("; ", result.Problems));

        var report = await new ConformanceRunner().RunAsync(new ConformanceOptions
        {
            PackageDirectory = result.PackageDirectory,
            Settings = new Dictionary<string, string> { ["ApiKey"] = "k" },
            Contracts = { ContractDocument.FromFile(Path.Combine(AppContext.BaseDirectory, "Contracts", "orders-adapter-contract.v1.json")) },
        });
        Assert.IsTrue(report.Passed, string.Join("; ", report.Checks.Where(c => c.Outcome == CheckOutcome.Failed)));
    }

    [TestMethod]
    public async Task A_secret_in_the_source_stops_the_build_until_it_is_allowed()
    {
        var project = AuthorProject(extraFiles: new Dictionary<string, string>
        {
            ["Defaults.cs"] = "class Defaults { const string ApiKey = \"live-9f8e7d6c5b4a3f2e\"; }",
        });

        var stopped = await Build(project);
        Assert.IsFalse(stopped.Succeeded);
        var problem = stopped.Problems.Single();
        StringAssert.Contains(problem, "Defaults.cs:1");
        StringAssert.Contains(problem, "--allow");
        Assert.IsNull(stopped.ZipPath, "nothing was built");

        var allowed = await Build(project, r => r.AllowedFiles.Add("Defaults.cs"));
        Assert.IsTrue(allowed.Succeeded, string.Join("; ", allowed.Problems));
    }

    [TestMethod]
    public async Task Without_source_the_package_carries_none()
    {
        var result = await Build(AuthorProject(), r => r.IncludeSource = false);

        Assert.IsTrue(result.Succeeded, string.Join("; ", result.Problems));
        Assert.IsNull(result.Manifest.Source);
        Assert.IsFalse(Directory.Exists(Path.Combine(result.PackageDirectory, "source")));
    }

    [TestMethod]
    public async Task A_dry_run_reports_the_source_and_builds_nothing()
    {
        var project = AuthorProject();
        var result = await Build(project, r => r.DryRun = true);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.SourceFiles.Any(f => f.Path.EndsWith("Program.cs")));
        Assert.IsNull(result.ZipPath);
        Assert.IsFalse(Directory.Exists(Path.Combine(project, "bin", "serverless")));
    }

    [TestMethod]
    public async Task A_gRPC_adapter_the_author_marks_classic_is_built_to_run_classically_over_gRPC()
    {
        var root = RepositoryRoot();
        var project = AuthorProject("""{ "id": "acme.grpc", "lifecycle": "classic" }""");
        File.Copy(Path.Combine(root, "SW.Serverless.UnitTests.GrpcClassicAdapter", "Program.cs"), Path.Combine(project, "Program.cs"), overwrite: true);

        var result = await Build(project);
        Assert.IsTrue(result.Succeeded, string.Join("; ", result.Problems));
        Assert.AreEqual(AdapterManifest.ClassicLifecycle, result.Manifest.Lifecycle);
        Assert.AreEqual(2, result.Manifest.Protocol!.Min);
    }
}
