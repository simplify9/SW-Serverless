using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// Which piece of state is being addressed. Scoped to the instance rather than to the adapter,
    /// because two instances of the same adapter are two different connections: one polling
    /// receiver's cursor must never be read by another.
    /// </summary>
    public class AdapterStateKey
    {
        public string AdapterId { get; set; }

        /// <summary>DataSourceId for an exclusive instance, the pool slot id for a pooled one.</summary>
        public string InstanceKey { get; set; }

        /// <summary>Chosen by the adapter. Namespace it yourself if one instance keeps several.</summary>
        public string Name { get; set; }

        public override string ToString() => $"{AdapterId}/{InstanceKey}/{Name}";
    }

    /// <summary>
    /// Implemented by the HOST APPLICATION, and the counterpart of <see cref="IAdapterEventSink"/>:
    /// where the sink is how an adapter hands work in, this is how it remembers where it got to.
    ///
    /// An adapter cannot hold its own progress. The supervisor restarts it, the next instance may
    /// come up on a different node, and a pooled one is not the same process twice — so a cursor
    /// kept in a field is a cursor that resets to the beginning at the least convenient moment.
    /// Bitween backs this with a table; a sample host can use <see cref="InMemoryAdapterStateStore"/>.
    ///
    /// Values are small — a bookmark, an offset, a timestamp. A host is entitled to refuse a large
    /// one, and should say so in the returned error rather than storing it.
    /// </summary>
    public interface IAdapterStateStore
    {
        /// <summary>Null when nothing is stored under that key.</summary>
        Task<string> GetAsync(AdapterStateKey key, CancellationToken cancellationToken);

        /// <summary>Durable before it returns. A null value deletes the entry.</summary>
        Task SetAsync(AdapterStateKey key, string value, CancellationToken cancellationToken);
    }

    /// <summary>
    /// The default, and only honest for a single-process host: state lives as long as the host does
    /// and is not shared between nodes. Registered so that samples and tests work out of the box —
    /// a real deployment replaces it, which is what the type parameter on
    /// <c>AddResidentAdapters</c> is for.
    /// </summary>
    public class InMemoryAdapterStateStore : IAdapterStateStore
    {
        readonly ConcurrentDictionary<string, string> entries = new();

        public Task<string> GetAsync(AdapterStateKey key, CancellationToken cancellationToken) =>
            Task.FromResult(entries.TryGetValue(key.ToString(), out var value) ? value : null);

        public Task SetAsync(AdapterStateKey key, string value, CancellationToken cancellationToken)
        {
            if (value == null) entries.TryRemove(key.ToString(), out _);
            else entries[key.ToString()] = value;

            return Task.CompletedTask;
        }
    }
}
