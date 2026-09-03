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
