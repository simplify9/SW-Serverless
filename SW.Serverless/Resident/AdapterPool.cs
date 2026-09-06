using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// Warm stateless workers, checked out per logical session. This is the shape that removes
    /// process spawn plus JIT from every request — nothing to do with brokers (design doc 14.5).
    /// Only for adapters that declare Poolable and implement IResettable.
    /// </summary>
    internal class AdapterPool : IAsyncDisposable
    {
        readonly AdapterSpec spec;
        readonly ResidentAdapterHost host;
        readonly ResidentOptions options;
        readonly ILogger logger;
        readonly SemaphoreSlim slots;
        readonly ConcurrentBag<ResidentAdapterInstance> idle = new();
        readonly ConcurrentDictionary<string, ResidentAdapterInstance> all = new();

        int created;

        public AdapterPool(AdapterSpec spec, ResidentAdapterHost host, ResidentOptions options, ILogger logger)
        {
            this.spec = spec;
            this.host = host;
            this.options = options;
            this.logger = logger;
            slots = new SemaphoreSlim(MaxInstances(spec));
        }

        static int MaxInstances(AdapterSpec spec) =>
            spec.AdapterValues != null &&
            spec.AdapterValues.TryGetValue("PoolSize", out var raw) &&
            int.TryParse(raw, out var n) && n > 0 ? n : 4;

        public async Task<IAdapterLease> RentAsync(CancellationToken cancellationToken)
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                if (!idle.TryTake(out var instance) || instance.State != InstanceState.Ready)
                {
                    var slot = $"pool-{Interlocked.Increment(ref created)}";
                    instance = await host.SpawnPooledAsync(spec, slot, cancellationToken);
                    all[slot] = instance;
                }

                return new Lease(this, instance);
            }
            catch
            {
                slots.Release();
                throw;
            }
        }

        async Task ReturnAsync(ResidentAdapterInstance instance, string sessionId)
        {
            try
            {
                if (instance.State == InstanceState.Ready)
                {
                    // The session boundary. Without it, process-static state — Traxis's LogStore
                    // is the live example — leaks from one request into the next.
                    await instance.ResetAsync(sessionId);
                    idle.Add(instance);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discarding pooled instance of {AdapterId} that failed to reset.", spec.AdapterId);
            }
            finally
            {
                slots.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var kv in all)
                await host.StopAsync(spec.AdapterId, kv.Key, drain: false);
            slots.Dispose();
        }

        sealed class Lease : IAdapterLease
        {
            readonly AdapterPool pool;
            public Lease(AdapterPool pool, ResidentAdapterInstance instance)
            {
                this.pool = pool;
                Instance = instance;
                SessionId = Guid.NewGuid().ToString("N");
            }

            public ResidentAdapterInstance Instance { get; }
            public string SessionId { get; }

            public Task<TResult> InvokeAsync<TResult>(string command, object input = null,
                int timeoutSeconds = 0, CancellationToken cancellationToken = default) =>
                Instance.InvokeAsync<TResult>(command, input, timeoutSeconds, cancellationToken, SessionId);

            public ValueTask DisposeAsync() => new(pool.ReturnAsync(Instance, SessionId));
        }
    }
}
