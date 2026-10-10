using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// Warm stateless workers, checked out per logical session. This is the shape that removes
    /// process spawn plus JIT from every request — nothing to do with brokers.
    /// Only for adapters that declare Poolable and implement IResettable.
    /// </summary>
    internal class AdapterPool : IAsyncDisposable
    {
        readonly AdapterSpec spec;
        readonly string slotPrefix;
        readonly ResidentAdapterHost host;
        readonly ResidentOptions options;
        readonly ILogger logger;
        readonly SemaphoreSlim slots;
        readonly ConcurrentBag<ResidentAdapterInstance> idle = new();
        readonly ConcurrentDictionary<string, ResidentAdapterInstance> all = new();

        int created;
        int rented;
        bool closed;
        readonly object gate = new();

        public AdapterPool(AdapterSpec spec, string poolKey, ResidentAdapterHost host, ResidentOptions options, ILogger logger)
        {
            this.spec = spec;
            slotPrefix = SlotPrefixOf(spec.AdapterId, poolKey);
            this.host = host;
            this.options = options;
            this.logger = logger;
            slots = new SemaphoreSlim(MaxInstances(spec));
        }

        const int MaxPoolSize = 64;

        /// <summary>
        /// Slot names carry the pool's identity. They were "pool-1", "pool-2"... in every pool, and
        /// the host registers slots by adapter id plus slot name — so two pools of one adapter
        /// (two subscriptions with different settings) overwrote each other's entries: retiring one
        /// pool's slot killed the other pool's process mid-call, the overwritten process went
        /// unsupervised, and both shared one state key. The pool with no startup values keeps the
        /// plain names it always had, so its stored state is still found.
        /// </summary>
        internal static string SlotPrefixOf(string adapterId, string poolKey)
        {
            if (string.IsNullOrEmpty(poolKey) || string.Equals(poolKey, adapterId, StringComparison.Ordinal))
                return "pool";

            var tag = poolKey.StartsWith(adapterId + ":", StringComparison.Ordinal)
                ? poolKey[(adapterId.Length + 1)..]
                : poolKey;
            var plain = tag.Length <= 32 && tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
            if (!plain)
                tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(tag)), 0, 8).ToLowerInvariant();
            return $"pool-{tag}";
        }

        /// <summary>
        /// Closes the pool if nothing is checked out and no instance is left, so the host can drop
        /// it. A pool that is closed hands out no more leases; the caller looks up a fresh one.
        /// </summary>
        internal bool TryClose()
        {
            lock (gate)
            {
                if (closed) return true;
                if (rented > 0 || !all.IsEmpty) return false;
                closed = true;
                return true;
            }
        }

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

        /// <summary>Per-adapter override for <see cref="ResidentOptions.IdleTimeout"/>, same shape as PoolSize.</summary>
        static TimeSpan IdleTimeoutFor(AdapterSpec spec, ResidentOptions options) =>
            spec.AdapterValues != null &&
            spec.AdapterValues.TryGetValue("IdleTimeoutSeconds", out var raw) &&
            double.TryParse(raw, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : options.IdleTimeout;

        /// <returns>Null when the pool has been closed; see <see cref="TryClose"/>.</returns>
        public async Task<IAdapterLease> RentAsync(CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (closed) return null;
                rented++;
            }

            try
            {
                await slots.WaitAsync(cancellationToken);
            }
            catch
            {
                lock (gate) rented--;
                throw;
            }

            try
            {
                string retiring = null;

                if (!idle.TryTake(out var instance) || instance.State != InstanceState.Ready)
                {
                    // A checked-out instance that is no longer Ready is dead weight: its process
                    // and its entry in the host's instance map both outlive it unless retired.
                    if (instance != null)
                        retiring = all.FirstOrDefault(kv => ReferenceEquals(kv.Value, instance)).Key;

                    var slot = $"{slotPrefix}-{Interlocked.Increment(ref created)}";
                    instance = await host.SpawnPooledAsync(spec, slot, cancellationToken);
                    all[slot] = instance;
                }

                if (retiring != null && all.TryRemove(retiring, out _))
                {
                    try { await host.RetireAsync(spec.AdapterId, retiring); }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not retire pooled slot {Slot}.", retiring); }
                }

                instance.IdleSince = null;
                return new Lease(this, instance);
            }
            catch
            {
                slots.Release();
                lock (gate) rented--;
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
                    instance.IdleSince = DateTimeOffset.UtcNow;
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
                lock (gate) rented--;
            }
        }

        /// <summary>
        /// Retires warm instances that have sat checked-in longer than the idle timeout, so a
        /// quiet pool shrinks back down instead of holding its peak size forever. Called
        /// periodically by the host's supervisor loop. A no-op
        /// when no idle timeout is configured for this adapter.
        /// </summary>
        public async Task EvictIdleAsync()
        {
            var idleTimeout = IdleTimeoutFor(spec, options);
            if (idleTimeout <= TimeSpan.Zero) return;

            var now = DateTimeOffset.UtcNow;
            var keep = new List<ResidentAdapterInstance>();
            var stale = new List<(string Slot, ResidentAdapterInstance Instance)>();

            // Drain-then-rebuild rather than inspecting in place: ConcurrentBag has no way to
            // remove a specific item, only to pop an arbitrary one. A RentAsync racing this sweep
            // may briefly see fewer idle instances than exist and spawn one it did not strictly
            // need to — self-correcting on the next return, and far cheaper than a lock around the
            // whole bag.
            while (idle.TryTake(out var instance))
            {
                var slot = instance.IdleSince.HasValue && now - instance.IdleSince.Value >= idleTimeout
                    ? all.FirstOrDefault(kv => ReferenceEquals(kv.Value, instance)).Key
                    : null;

                if (slot != null) stale.Add((slot, instance));
                else keep.Add(instance);
            }

            foreach (var instance in keep) idle.Add(instance);

            foreach (var (slot, instance) in stale)
            {
                if (!all.TryRemove(slot, out _)) continue;

                logger.LogInformation(
                    "Retiring idle pooled instance {AdapterId}/{Slot}: idle for {Idle}, timeout is {Timeout}.",
                    spec.AdapterId, slot, now - instance.IdleSince.Value, idleTimeout);

                try { await host.RetireAsync(spec.AdapterId, slot); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not retire idle pooled slot {Slot}.", slot); }
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
                int timeoutSeconds = 0, CancellationToken cancellationToken = default,
                IDictionary<string, string> properties = null) =>
                Instance.InvokeAsync<TResult>(command, input, timeoutSeconds, cancellationToken,
                    SessionId, properties);

            public ValueTask DisposeAsync() => new(pool.ReturnAsync(Instance, SessionId));
        }
    }
}
