using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.PrimitiveTypes;
using SW.Serverless.Contract.Catalog;

namespace SW.Serverless.Installer.UnitTests;

[TestClass]
public class PinnedMetadataCacheTests
{
#pragma warning disable CS0618 // MemoryCacheOptions.Clock is how a test moves the cache's time
    sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }
#pragma warning restore CS0618

    static Task Put(ObjectStore store, string key, string content) =>
        store.WriteAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), new WriteFileSettings
        {
            Key = key,
            Metadata = new Dictionary<string, string> { ["EntryAssembly"] = "Adapter.dll" }
        });

    [TestMethod]
    public async Task A_pinned_version_is_resolved_once_and_a_current_package_again_after_the_cache_duration()
    {
        using var store = new ObjectStore();
        var clock = new TestClock();
#pragma warning disable CS0618
        var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
#pragma warning restore CS0618
        var options = new ServerlessOptions { AdapterRemotePath = "adapters", AdapterMetadataCacheDuration = 5 };
        var installer = new AdapterInstaller(options, cache, store);
        var versionKey = AdapterCatalogPaths.Version("adapters", "acme.x", "1.0.0");

        await Put(store, "adapters/acme.x", "current 1");
        await Put(store, versionKey, "version 1");
        var current = await installer.GetMetadataAsync("acme.x");
        var pinned = await installer.GetMetadataAsync("acme.x/1.0.0");

        // A new current package; and, to make the cache observable, the version rewritten too
        // (a published version never is).
        await Put(store, "adapters/acme.x", "current 2");
        await Put(store, versionKey, "version 1, rewritten");
        clock.UtcNow += TimeSpan.FromMinutes(6);

        Assert.AreNotEqual(current.Hash, (await installer.GetMetadataAsync("acme.x")).Hash, "the current package is read again");
        Assert.AreEqual(pinned.Hash, (await installer.GetMetadataAsync("acme.x/1.0.0")).Hash, "the pinned version is not");
    }
}
