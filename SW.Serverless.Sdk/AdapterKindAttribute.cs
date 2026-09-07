using System;

namespace SW.Serverless.Sdk
{
    /// <summary>
    /// Declares what an adapter is FOR, so a host can offer it in the right place without being
    /// told separately.
    ///
    /// The kind is a label this framework does not interpret — a host decides what "handler" or
    /// "mapper" means to it. Declaring it here rather than encoding it in the adapter's id is what
    /// lets an adapter be reclassified without being renamed, and a rename is not free: hosts store
    /// the id against every configuration that uses the adapter.
    ///
    /// Apply more than once for an adapter that serves in several roles.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class AdapterKindAttribute : Attribute
    {
        public AdapterKindAttribute(string kind) => Kind = kind;

        /// <summary>e.g. "handler", "mapper", "receiver", "validator".</summary>
        public string Kind { get; }
    }
}
