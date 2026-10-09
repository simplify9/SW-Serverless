using System.Collections.Generic;
using System.Linq;

namespace SW.Serverless.Tooling.Conformance
{
    public enum CheckOutcome { Passed, Failed, Skipped }

    /// <summary>One thing the kit checked, and what it found.</summary>
    public record ConformanceCheck(string Name, CheckOutcome Outcome, string Detail = null);

    public class ConformanceReport
    {
        public List<ConformanceCheck> Checks { get; } = new();

        public bool Passed => Checks.All(c => c.Outcome != CheckOutcome.Failed);

        internal void Pass(string name, string detail = null) => Checks.Add(new(name, CheckOutcome.Passed, detail));
        internal void Fail(string name, string detail) => Checks.Add(new(name, CheckOutcome.Failed, detail));
        internal void Skip(string name, string detail) => Checks.Add(new(name, CheckOutcome.Skipped, detail));
    }
}
