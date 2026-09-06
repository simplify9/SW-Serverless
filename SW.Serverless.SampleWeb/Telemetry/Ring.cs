using System.Collections.Generic;
using System.Linq;

namespace SW.Serverless.SampleWeb.Telemetry
{
    /// <summary>Fixed-size newest-first buffer. Observability must never grow without bound.</summary>
    public class Ring<T>
    {
        readonly LinkedList<T> items = new();
        readonly int capacity;
        readonly object gate = new();

        public Ring(int capacity) => this.capacity = capacity;

        public long Total { get; private set; }

        public void Add(T item)
        {
            lock (gate)
            {
                items.AddFirst(item);
                Total++;
                while (items.Count > capacity) items.RemoveLast();
            }
        }

        public IReadOnlyList<T> Snapshot(int take = int.MaxValue)
        {
            lock (gate) return items.Take(take).ToList();
        }

        public void Clear()
        {
            lock (gate) { items.Clear(); Total = 0; }
        }
    }
}
