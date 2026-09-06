using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
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

        const int MaxPoolSize = 64;

        /// <summary>
        /// Clamped, because PoolSize comes from adapter metadata rather than from code. An
        /// unbounded value would have the host spawn that many processes on a single node.
        /// </summary>
        static int MaxInstances(AdapterSpec spec) =>
            spec.AdapterValues != null &&
            spec.AdapterValues.TryGetValue("PoolSize", out var raw) &&
            int.TryParse(raw, out var n) && n > 0
                ? Math.Min(n, MaxPoolSize)
                : 4;

        public async Task<IAdapterLease> RentAsync(CancellationToken cancellationToken)
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                string retiring = null;

                if (!idle.TryTake(out var instance) || instance.State != InstanceState.Ready)
                {
                    // A checked-out instance that is no longer Ready is dead weight: its process
                    // and its entry in the host's instance map both outlive it unless retired.
                    if (instance != null)
                        retiring = all.FirstOrDefault(kv => ReferenceEquals(kv.Value, instance)).Key;

                    var slot = $"pool-{Interlocked.Increment(ref created)}";
                    instance = await host.SpawnPooledAsync(spec, slot, cancellationToken);
                    all[slot] = instance;
                }

                if (retiring != null && all.TryRemove(retiring, out _))
                {
                    try { await host.RetireAsync(spec.AdapterId, retiring); }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not retire pooled slot {Slot}.", retiring); }
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
                    // The session boundary, and it is AWAITED. Without waiting for the adapter to
                    // confirm, the instance went back to idle before the reset had been applied
                    // and the next lease could see the previous session's state — the exact leak
                    // this boundary exists to prevent.
                    await instance.ResetAsync(sessionId);
                    idle.Add(instance);
                }
            }
            catch (Exception ex)
            {
                // Not returned to idle: an instance that cannot confirm a reset is not safe to
                // hand to another session. It stays out of the pool and a fresh one takes its slot.
                logger.LogWarning(ex, "Discarding pooled instance of {AdapterId} that failed to reset.", spec.AdapterId);

                var slot = all.FirstOrDefault(kv => ReferenceEquals(kv.Value, instance)).Key;
                if (slot != null && all.TryRemove(slot, out _))
                {
                    try { await host.RetireAsync(spec.AdapterId, slot); } catch { }
                }
            }
            finally
            {
                if (!disposed) slots.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var kv in all)
                await host.StopAsync(spec.AdapterId, kv.Key, drain: false);

            // NOT disposed: a lease still in flight will call Release on the way out, and
            // disposing here made that throw ObjectDisposedException during shutdown. The
            // semaphore is cheap to leave to the GC.
            disposed = true;
        }

        bool disposed;

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
