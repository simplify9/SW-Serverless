using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SW.PrimitiveTypes;

namespace SW.Serverless.Installer.UnitTests;

/// <summary>
/// An in-memory store that behaves like S3, which is what most deployments run on: keys are flat,
/// so <c>adapters/foo</c> and <c>adapters/foo/1.0.0</c> coexist — which the filesystem provider
/// cannot do, and which is exactly the layout an older installer left behind. Metadata comes back
/// the way the S3 provider returns it: Hash from the ETag first, then user metadata with lower-cased
/// names, a written Hash ignored.
/// </summary>
public sealed class ObjectStore : ICloudFilesService, IDisposable
{
    record Item(byte[] Content, Dictionary<string, string> Metadata);

    readonly ConcurrentDictionary<string, Item> items = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Keys => items.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>Registers this store with the installer under a unique provider name, and returns the name.</summary>
    public string Register()
    {
        var name = "objectstore-" + Guid.NewGuid().ToString("N");
        Shared.CloudFilesFactory.Register(name, _ => this);
        return name;
    }

    public async Task<RemoteBlob> WriteAsync(Stream inputStream, WriteFileSettings settings)
    {
        using var buffer = new MemoryStream();
        await inputStream.CopyToAsync(buffer);
        items[settings.Key] = new Item(buffer.ToArray(),
            (settings.Metadata ?? new Dictionary<string, string>())
            .ToDictionary(m => m.Key.ToLowerInvariant(), m => m.Value));
        return new RemoteBlob { Name = settings.Key, Location = settings.Key, Size = (int)buffer.Length };
    }

    public Task<RemoteBlob> WriteTextAsync(string text, WriteFileSettings settings) =>
        WriteAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), settings);

    public Task<Stream> OpenReadAsync(string key) =>
        items.TryGetValue(key, out var item)
            ? Task.FromResult<Stream>(new MemoryStream(item.Content, writable: false))
            : throw new FileNotFoundException(key);

    public Task<IEnumerable<CloudFileInfo>> ListAsync(string prefix) =>
        Task.FromResult<IEnumerable<CloudFileInfo>>(items
            .Where(i => i.Key.StartsWith(prefix ?? "", StringComparison.Ordinal))
            .Select(i => new CloudFileInfo { Key = i.Key, Size = i.Value.Content.Length })
            .ToList());

    public Task<IReadOnlyDictionary<string, string>> GetMetadataAsync(string key)
    {
        if (!items.TryGetValue(key, out var item)) throw new FileNotFoundException(key);

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hash"] = Convert.ToHexString(MD5.HashData(item.Content)).ToLowerInvariant(),
            ["ContentLength"] = item.Content.Length.ToString(),
        };
        foreach (var m in item.Metadata) metadata.TryAdd(m.Key, m.Value);
        return Task.FromResult<IReadOnlyDictionary<string, string>>(metadata);
    }

    public Task<bool> DeleteAsync(string key) => Task.FromResult(items.TryRemove(key, out _));

    /// <summary>Replaces a package's bytes behind the installer's back, keeping its metadata.</summary>
    public void Tamper(string key) =>
        items[key] = items[key] with { Content = items[key].Content.Concat(new byte[] { 0 }).ToArray() };

    public string GetUrl(string key) => key;
    public string GetSignedUrl(string key, TimeSpan expiry) => key;
    public WriteWrapper OpenWrite(WriteFileSettings settings) => throw new NotSupportedException();
    public void Dispose() { }
}
