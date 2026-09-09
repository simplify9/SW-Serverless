using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Resident
{
    /// <summary>A pooled instance checked out for one logical session. Dispose returns it.</summary>
    public interface IAdapterLease : IAsyncDisposable
    {
        ResidentAdapterInstance Instance { get; }
        string SessionId { get; }

        /// <summary>
        /// Invoke with this lease's session id attached, so several calls in one checkout share a
        /// session — which is what makes the call-a-command-then-GetLogs pattern work, and what
        /// disposal then clears through IResettable.
        /// </summary>
        Task<TResult> InvokeAsync<TResult>(string command, object input = null,
            int timeoutSeconds = 0, CancellationToken cancellationToken = default,
            IDictionary<string, string> properties = null);
    }

    /// <summary>
    /// Owns every long-lived adapter process on this node. Registered as a SINGLETON — a request
    /// scope ending must never kill a broker connection (design doc 4).
    /// </summary>
    public interface IResidentAdapterHost
    {
        /// <summary>
        /// Exactly one instance for this key, for as long as it is wanted. This is the shape a
        /// broker connection takes; placement and leader election decide who calls it.
        /// </summary>
        Task<ResidentAdapterInstance> StartExclusiveAsync(AdapterSpec spec, CancellationToken cancellationToken = default);

        Task StopAsync(string adapterId, string instanceKey, bool drain = true, CancellationToken cancellationToken = default);

        /// <summary>
        /// Changes the ceilings a running adapter is held to.
        ///
        /// The soft memory and CPU ceilings are read on every sample, so they are in force
        /// immediately. The hard memory ceiling is the runtime's own GC heap limit and is fixed at
        /// launch, so a change to it is reported as needing a restart rather than quietly ignored —
        /// the caller decides whether that happens now or at a quieter moment.
        /// </summary>
        Task<LimitUpdate> UpdateLimitsAsync(string adapterId, string instanceKey,
            ResourceLimits limits, CancellationToken cancellationToken = default);

        /// <summary>
        /// Relaunches an instance in place, keeping its registry entry — so a lease, a data source,
        /// or anything else holding the key still points at it afterwards. Stopping and starting
        /// instead drops the entry, which in Bitween's case would release the broker lease that
        /// makes the connection exclusive.
        /// </summary>
        Task<ResidentAdapterInstance> RestartAsync(string adapterId, string instanceKey,
            bool drain = true, CancellationToken cancellationToken = default);

        /// <summary>
        /// Check out one warm instance from a pool of stateless workers. Replaces a per-invocation
        /// process spawn. Only for adapters declaring Poolable — a process-static field would
        /// otherwise leak across sessions (design doc 14.5).
        /// </summary>
        Task<IAdapterLease> RentAsync(AdapterSpec spec, CancellationToken cancellationToken = default);

        ResidentAdapterInstance Get(string adapterId, string instanceKey);
        IReadOnlyCollection<ResidentAdapterInstance> List();

        /// <summary>Health of every instance on this node, host-observed and adapter-reported.</summary>
        IReadOnlyCollection<InstanceHealth> Describe();
    }
}
