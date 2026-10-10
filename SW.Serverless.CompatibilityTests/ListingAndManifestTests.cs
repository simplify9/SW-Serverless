using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.PrimitiveTypes;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Tooling;

namespace SW.Serverless.CompatibilityTests;

/// <summary>
/// (d) Applications older than the catalog list adapters by listing storage. Whatever the current
/// installer writes must look to them like exactly the adapters that exist — no catalog file, no
/// version folder, no stray id.
/// </summary>
[TestClass]
public class OldListingTests
{
    const string Sample = "SW.Serverless.Samples.Classic";

    /// <summary>The version test deployed listings use, as shipped.</summary>
    static bool IsVersionNumber(string text) => Regex.IsMatch(text, @"^\d+\.\d+\.\d+(-\S+)?$");

    /// <summary>
    /// The grouping deployed listings use, copied as shipped: non-empty
    /// keys, a semver last segment grouped under the segment before it, anything else an adapter id.
    /// </summary>
    static async Task<Dictionary<string, List<string>>> OldListingAsync(ICloudFilesService files, string prefix)
    {
        var index = "adapters".Length + 1;
        return (await files.ListAsync(prefix))
            .Where(item => item.Size > 0)
            .GroupBy(i =>
            {
                var lastSection = i.Key.Split("/").Last();
                return IsVersionNumber(lastSection) ? i.Key.Split("/").ElementAt(^2) : lastSection;
            })
            .ToDictionary(g => g.Key, g => g
                .Where(v => v.Key != g.Key && IsVersionNumber(v.Key.Split("/").Last()))
                .Select(v => v.Key[index..])
                .ToList());
    }

    [TestMethod]
    public async Task An_old_listing_sees_exactly_the_adapter_after_versions_promote_and_withdraw()
    {
        using var bucket = new Bucket();
        // Named by the old convention, so both of the old listings — by convention prefix,
        // and everything under adapters/ — are exercised.
        const string id = "infolink6.handlers.compat";

        await Compat.PublishAsync(bucket, Sample, id, "1.0.0");
        await Compat.PublishAsync(bucket, Sample, id, "minor");
        var repository = new AdapterRepository(bucket.Files, _ => { });
        await repository.PromoteAsync(id, "1.0.0", Path.Combine(bucket.Path, "..", "work-" + Guid.NewGuid().ToString("N")));
        await repository.WithdrawAsync(id, "1.1.0");
        await Compat.PublishAsync(bucket, Sample, id, "patch", promote: false);

        foreach (var prefix in new[] { "adapters/", "adapters/infolink6.handlers" })
        {
            var listed = await OldListingAsync(bucket.Files, prefix);
            CollectionAssert.AreEqual(new[] { id }, listed.Keys.ToList(), $"listing {prefix}");
            Assert.AreEqual(0, listed[id].Count, "no version is under adapters/ for an old listing to offer");
        }

        CollectionAssert.AreEqual(new[] { $"adapters/{id}" }, await bucket.KeysAsync("adapters/"),
            "nothing new may be written under adapters/");
        CollectionAssert.AreEqual(new[]
        {
            $"adapters-versions/{id}/1.0.0",
            $"adapters-versions/{id}/1.1.0",
            $"adapters-versions/{id}/1.1.1",
        }, await bucket.KeysAsync("adapters-versions/"));
        CollectionAssert.AreEqual(new[] { $"adapters-catalog/{id}.json" }, await bucket.KeysAsync("adapters-catalog/"));
    }
}

/// <summary>(e) A manifest or catalog entry written by a NEWER tool survives a round trip through this one.</summary>
[TestClass]
public class ManifestForwardCompatibilityTests
{
    const string Future = """
        {
          "manifestVersion": 3,
          "id": "future.adapter",
          "version": "4.0.0",
          "displayName": "From the future",
          "runtime": "wasm",
          "lifecycle": "classic",
          "signature": { "algorithm": "ed25519", "value": "abc==" },
          "commands": [ { "name": "Echo", "input": { "type": "string" } } ],
          "minimumMemoryMb": 256,
          "properties": [ { "name": "Url", "type": "text", "required": true } ]
        }
        """;

    [TestMethod]
    public void Unknown_manifest_fields_round_trip_without_loss()
    {
        var manifest = AdapterManifest.Parse(Future);
        CollectionAssert.AreEquivalent(new[] { "signature", "commands", "minimumMemoryMb" }, manifest.Extensions!.Keys.ToList());

        var again = AdapterManifest.Parse(manifest.ToJson());
        Assert.AreEqual(3, again.ManifestVersion);
        Assert.AreEqual("wasm", again.Runtime);
        Assert.AreEqual("From the future", again.DisplayName);
        Assert.AreEqual(1, again.Properties.Count);

        foreach (var field in new[] { "signature", "commands", "minimumMemoryMb" })
            Assert.IsTrue(JsonElement.DeepEquals(
                    JsonDocument.Parse(Future).RootElement.GetProperty(field), again.Extensions![field]),
                $"{field} changed on the round trip");
    }

    [TestMethod]
    public void Unknown_fields_inside_nested_objects_round_trip_too()
    {
        const string nested = """
            {
              "publisher": { "name": "Acme", "verified": true },
              "compatibility": { "minHostVersion": "10.0.2", "maxHostVersion": "12.0.0" },
              "properties": [ { "name": "Endpoint", "pattern": "^https://" } ]
            }
            """;

        var again = AdapterManifest.Parse(AdapterManifest.Parse(nested).ToJson());

        Assert.IsTrue(again.Publisher!.Extensions!.ContainsKey("verified"));
        Assert.IsTrue(again.Compatibility!.Extensions!.ContainsKey("maxHostVersion"));
        Assert.AreEqual("^https://", again.Properties[0].Extensions!["pattern"].GetString());
    }

    [TestMethod]
    public void Unknown_catalog_fields_round_trip_without_loss()
    {
        const string json = """
            {
              "catalogVersion": 2, "id": "future.adapter", "current": "4.0.0",
              "channels": { "beta": "4.1.0-rc.1" },
              "versions": [ { "version": "4.0.0", "sha256": "ab", "attestation": { "by": "ci" } } ]
            }
            """;

        var again = AdapterCatalogEntry.Parse(AdapterCatalogEntry.Parse(json).ToJson());
        Assert.AreEqual("4.1.0-rc.1", again.Extensions!["channels"].GetProperty("beta").GetString());
        Assert.AreEqual("ci", again.Versions[0].Extensions!["attestation"].GetProperty("by").GetString());
    }

    /// <summary>The installer merges over an author file from a newer tool, keeping what it does not know.</summary>
    [TestMethod]
    public void The_installer_keeps_unknown_author_fields()
    {
        var merged = ManifestBuilder.Build(AdapterManifest.Parse(Future), new GeneratedManifestFields
        {
            Id = "future.adapter",
            Version = "1.0.0",
            Entry = "Future.dll",
        });

        var written = AdapterManifest.Parse(merged.ToJson());
        Assert.AreEqual(3, written.ManifestVersion, "a newer manifestVersion is never lowered");
        Assert.AreEqual("dotnet", written.Runtime);
        Assert.IsTrue(written.Extensions!.ContainsKey("signature"));
        Assert.IsTrue(written.Extensions.ContainsKey("commands"));
    }
}
