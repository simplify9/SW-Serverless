using SW.Serverless.Sdk.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SW.Serverless.Samples.Carrier
{
    /// <summary>
    /// The audit trail of every upstream call, which is what Traxis's GetLogs returns and what
    /// ends up in the S3 record for a shipment.
    ///
    /// In Traxis this is a process-STATIC list, and that is safe there only because a classic
    /// adapter serves exactly one caller before exiting. Pool the process and one request's
    /// carrier calls leak into the next request's audit log.
    ///
    /// Keying by session is the fix. Entries belong to <see cref="AdapterSession.Id"/>, the host
    /// clears them at the session boundary through IResettable, and nothing can bleed across.
    /// </summary>
    public class CallLog
    {
        readonly ConcurrentDictionary<string, List<CallLogEntry>> bySession = new();

        const string NoSession = "(none)";

        static string Key => AdapterSession.Id ?? NoSession;

        public void Record(string operation, TimeSpan elapsed, bool succeeded, string detail)
        {
            var entries = bySession.GetOrAdd(Key, _ => new List<CallLogEntry>());
            lock (entries)
                entries.Add(new CallLogEntry
                {
                    On = DateTimeOffset.UtcNow,
                    Operation = operation,
                    Milliseconds = (int)elapsed.TotalMilliseconds,
                    Succeeded = succeeded,
                    Detail = detail
                });
        }

        public CallLogResult Current()
        {
            var key = Key;
            if (!bySession.TryGetValue(key, out var entries))
                return new CallLogResult { SessionId = key };

            lock (entries)
                return new CallLogResult { SessionId = key, Entries = entries.ToList() };
        }

        /// <summary>Called at the session boundary. Without this, pooling would not be safe.</summary>
        public void Clear(string sessionId) => bySession.TryRemove(sessionId ?? NoSession, out _);

        public int SessionCount => bySession.Count;
    }
}
