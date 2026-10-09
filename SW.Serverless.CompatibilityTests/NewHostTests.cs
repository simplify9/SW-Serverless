using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Resident;

namespace SW.Serverless.CompatibilityTests;

/// <summary>
/// (b) The current host runs packages published the OLD way — a zip and its metadata, no
/// adapter.json, no catalog — exactly as before.
/// </summary>
[TestClass]
public class NewHostOldPackagesTests
{
    const string Adapter = "SW.Serverless.UnitTests.Adapter";

    [TestMethod]
    public async Task A_package_without_a_manifest_installs_and_runs()
    {
        using var bucket = new Bucket();
        await Compat.PublishTheOldWayAsync(bucket.Files, "adapters/old.package", Adapter);

        CollectionAssert.AreEqual(new[] { "adapters/old.package" }, await bucket.KeysAsync(),
            "the fixture must look like an old installer's output: no manifest, no catalog");

        using var host = Compat.NewHost(bucket);
        using var service = (ServerlessService)host.GetRequiredService<IServerlessService>();
        await service.StartAsync("old.package", "correlation");

        Assert.AreEqual("value", await service.InvokeAsync<string>("TestString", "value"));
        Assert.AreEqual("42", await service.InvokeAsync<string>("TestInt", 42));
        var blob = await service.InvokeAsync<RemoteBlob>("TestTypedReturn", null);
        Assert.AreEqual("http://sample.com", blob.Location);

        var expected = await service.GetExpectedStartupValues();
        Assert.AreEqual(2, expected.Count);
        Assert.IsTrue(expected["UserName"].Optional);
        Assert.AreEqual("admin", expected["UserName"].Default);
        Assert.IsFalse(expected["Password"].Optional);
    }

    [TestMethod]
    public async Task A_version_an_old_installer_published_still_resolves_by_pinned_ref()
    {
        using var bucket = new Bucket();
        await Compat.PublishTheOldWayAsync(bucket.Files, "adapters/old.versioned/1.2.0", Adapter);

        using var host = Compat.NewHost(bucket);
        using var service = (ServerlessService)host.GetRequiredService<IServerlessService>();
        await service.StartAsync("old.versioned/1.2.0", "correlation");
        Assert.AreEqual("pinned", await service.InvokeAsync<string>("TestString", "pinned"));
    }
}

/// <summary>
/// (c) Adapters built against the PUBLISHED SDK 10.0.0 — classic and resident — run on the
/// current host, and the current installer describes and probes them correctly.
/// </summary>
[TestClass]
public class OldSdkOnNewHostTests
{
    const string ClassicV10 = "SW.Serverless.Compat.ClassicV10";
    const string ResidentV10 = "SW.Serverless.Compat.ResidentV10";

    [TestMethod]
    public async Task A_classic_adapter_on_sdk_10_0_0_runs_on_the_current_host()
    {
        using var bucket = new Bucket();
        var published = await Compat.PublishAsync(bucket, ClassicV10, "sdk10.classic", "1.0.0", probe: true);

        // Described and probed through the 10.0.0 SDK's own answers.
        Assert.AreEqual(AdapterManifest.ClassicLifecycle, published.Manifest.Lifecycle);
        CollectionAssert.AreEqual(new[] { "handler" }, published.Manifest.Kinds);
        Assert.AreEqual("10.0.0", published.Manifest.SdkVersion);
        var properties = published.Manifest.Properties.ToDictionary(p => p.Name);
        Assert.AreEqual("hello", properties["Greeting"].Default);
        Assert.IsFalse(properties["Greeting"].Required);
        Assert.IsTrue(properties["Token"].Required);
        Assert.IsTrue(properties["Token"].Secret);

        using var host = Compat.NewHost(bucket);
        using var service = (ServerlessService)host.GetRequiredService<IServerlessService>();
        await service.StartAsync("sdk10.classic", "correlation", new Dictionary<string, string> { ["Greeting"] = "hi" });

        Assert.AreEqual("echo", await service.InvokeAsync<string>("Echo", "echo"));
        Assert.AreEqual("hi, there", await service.InvokeAsync<string>("Greet", "there"));
        var expected = await service.GetExpectedStartupValues();
        Assert.IsTrue(expected["Token"].Private);
    }

    [TestMethod]
    public async Task A_resident_adapter_on_sdk_10_0_0_runs_on_the_current_host()
    {
        using var bucket = new Bucket();
        var published = await Compat.PublishAsync(bucket, ResidentV10, "sdk10.resident", "1.0.0");
        Assert.AreEqual(AdapterManifest.ResidentLifecycle, published.Manifest.Lifecycle);
        CollectionAssert.AreEqual(new[] { "receiver" }, published.Manifest.Kinds);

        // A socket path of its own: other suites on this machine run resident hosts too.
        var tag = $"{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s =>
            {
                s.AddLocalTestsCloudFiles(o =>
                {
                    o.BucketName = bucket.Name;
                    o.StoragePath = bucket.Path;
                });
                s.AddServerless(o =>
                {
                    o.AdapterRemotePath = "adapters";
                    o.AdapterLocalPath = Path.Combine(bucket.Path, "..", "installed-" + tag);
                    o.AdapterMetadataCacheDuration = 1;
                });
                s.AddResidentAdapters<NullSink>(o =>
                {
                    o.SocketPath = $"/tmp/swsl-compat-{tag}.sock";
                    o.PipeName = $"swsl-compat-{tag}";
                    o.HeartbeatInterval = TimeSpan.FromSeconds(2);
                    o.HandshakeTimeout = TimeSpan.FromSeconds(30);
                });
            })
            .Build();

        await host.StartAsync();
        try
        {
            var adapters = host.Services.GetRequiredService<IResidentAdapterHost>();
            var instance = await adapters.StartExclusiveAsync(new AdapterSpec
            {
                AdapterId = "sdk10.resident",
                InstanceKey = "compat",
                StartupValues = { ["Prefix"] = "v10:" },
            });

            Assert.AreEqual(InstanceState.Ready, instance.State);
            Assert.AreEqual("v10:ping", await instance.InvokeAsync<string>("Echo", "ping"));

            // The 10.0.0 SDK knows nothing of the language-neutral Hello fields: they're simply
            // empty, and nothing depends on them being there.
            var described = adapters.Describe().Single(h => h.InstanceKey == "compat");
            Assert.AreEqual("", described.SdkLanguage);
            Assert.AreEqual(0, described.Settings.Count);
            Assert.AreEqual(0, described.Kinds.Count);
            Assert.AreEqual(0, described.Contracts.Count);

            await adapters.StopAsync("sdk10.resident", "compat", drain: false);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    sealed class NullSink : IAdapterEventSink
    {
        public Task<EventOutcome> OnEventAsync(InboundEvent inboundEvent, CancellationToken cancellationToken) =>
            Task.FromResult(EventOutcome.Ok("compat"));
    }
}
