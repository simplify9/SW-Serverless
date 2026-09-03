using System.Collections.Concurrent;

namespace SW.Serverless.Resident
{
    /// <summary>Holds instances awaiting attach, keyed by their one-time token.</summary>
    internal class ResidentAdapterRegistry
    {
        readonly ConcurrentDictionary<string, ResidentAdapterInstance> awaiting = new();

        public void Expect(ResidentAdapterInstance instance) => awaiting[instance.Token] = instance;

        public ResidentAdapterInstance Claim(string token) =>
            token != null && awaiting.TryRemove(token, out var i) ? i : null;

        public void Forget(string token) => awaiting.TryRemove(token, out _);
    }
}
