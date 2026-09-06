using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Sdk.Resident
{
    /// <summary>
    /// Implemented by an adapter that stays running. Command methods are still discovered by
    /// reflection exactly as in the classic runner, so a handler can do both.
    /// </summary>
    public interface IResidentAdapter
    {
        /// <summary>
        /// Called once, after the host has sent startup values. Open your connection here and
        /// return — do not block. Long-lived work belongs on your own task.
        /// </summary>
        Task StartAsync(IAdapterContext context, CancellationToken cancellationToken);

        /// <summary>
        /// Stop fetching, finish or nack what is in flight, close the connection.
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken);

        /// <summary>Answered on every heartbeat. Must return promptly even while busy.</summary>
        Task<AdapterStatus> GetStatusAsync();
    }

    /// <summary>
    /// Optional. Implement on a POOLED adapter to clear per-session state between checkouts —
    /// without this, process-static state leaks across requests (design doc, section 14.5).
    /// </summary>
    public interface IResettable
    {
        Task ResetAsync(string sessionId);
    }
}
