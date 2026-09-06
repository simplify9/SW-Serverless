using System;
using System.Threading;

namespace SW.Serverless.Sdk.Hosting
{
    /// <summary>
    /// Ambient per-invocation identity. Set by the runner around every command, so a service deep
    /// in the call graph can tag its work without every method taking a correlation id.
    ///
    /// For a POOLED adapter this is also the boundary that matters: state keyed by
    /// <see cref="Id"/> cannot leak into the next checkout the way a process-static field does.
    /// </summary>
    public static class AdapterSession
    {
        static readonly AsyncLocal<SessionState> current = new();

        public static string Id => current.Value?.Id;
        public static string Command => current.Value?.Command;
        public static bool InSession => current.Value != null;

        public static IDisposable Begin(string id, string command)
        {
            var previous = current.Value;
            current.Value = new SessionState { Id = id, Command = command };
            return new Scope(previous);
        }

        sealed class SessionState
        {
            public string Id;
            public string Command;
        }

        sealed class Scope : IDisposable
        {
            readonly SessionState previous;
            public Scope(SessionState previous) => this.previous = previous;
            public void Dispose() => current.Value = previous;
        }
    }
}
