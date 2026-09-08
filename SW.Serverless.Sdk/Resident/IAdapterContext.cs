using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Sdk.Resident
{
    public class PublishResult
    {
        public bool Accepted { get; set; }

        /// <summary>The host's id for what it persisted, e.g. an Xchange id.</summary>
        public string Reference { get; set; }

        public string Error { get; set; }
    }

    /// <summary>
    /// Everything a resident adapter is given by the host. The adapter never touches the
    /// database, cloud storage or the internal bus — it hands work to the host and waits.
    /// </summary>
    public interface IAdapterContext
    {
        string AdapterId { get; }
        string InstanceKey { get; }

        IReadOnlyDictionary<string, string> StartupValues { get; }
        IReadOnlyDictionary<string, string> AdapterValues { get; }
        string StartupValueOf(string name);

        /// <summary>Cancelled when the host asks the adapter to shut down.</summary>
        CancellationToken Stopping { get; }

        /// <summary>
        /// Push one inbound message to the host and WAIT for it to be durably persisted.
        /// Do not acknowledge your broker until this returns Accepted.
        /// Concurrency is bounded by the host's max-in-flight grant.
        /// </summary>
        Task<PublishResult> PublishAsync(
            ReadOnlyMemory<byte> payload,
            string dedupeKey,
            string endpoint = null,
            IDictionary<string, string> headers = null,
            string contentType = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads a small piece of durable state the HOST holds on this adapter's behalf. Null when
        /// nothing is stored under that name.
        ///
        /// An adapter must not be the system of record for its own progress: the supervisor
        /// restarts it, the next instance may be on another node, and a pooled one is not even the
        /// same process twice. A polling receiver's cursor is the case this exists for — the same
        /// role Airbyte's `state` argument plays. Keep it to a bookmark; it is not a data store,
        /// and the host is entitled to refuse a large value.
        /// </summary>
        Task<string> GetStateAsync(string name, CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes that state, durably, before returning. A null value deletes it.
        ///
        /// Call it at the point the progress is real — after the rows it describes have been
        /// accepted by the host — because anything written earlier is a promise the next instance
        /// will believe.
        /// </summary>
        Task SetStateAsync(string name, string value, CancellationToken cancellationToken = default);

        void Log(AdapterLogLevel level, string message, Exception exception = null,
            IDictionary<string, string> properties = null);

        void LogInformation(string message, IDictionary<string, string> properties = null);
        void LogWarning(string message, Exception exception = null);
        void LogError(string message, Exception exception = null);

        /// <summary>Reported on a schedule; the host republishes on System.Diagnostics.Metrics.</summary>
        void Metric(string name, double value, IDictionary<string, string> tags = null);

        /// <summary>Runtime-changeable by the host, per instance. Check before building costly logs.</summary>
        AdapterLogLevel MinimumLogLevel { get; }
    }

    public enum AdapterLogLevel
    {
        Trace = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Critical = 5
    }
}
