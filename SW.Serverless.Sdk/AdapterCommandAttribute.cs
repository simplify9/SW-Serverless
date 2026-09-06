using System;

namespace SW.Serverless.Sdk
{
    /// <summary>
    /// Describes a command in words, so what an adapter exposes can be read without its source.
    ///
    /// Entirely optional — every public Task-returning method is discovered and callable without
    /// it. What this adds is the difference between a list of names and a list an operator can act
    /// on: "GetStats" tells you nothing about whether it is safe to click during an incident.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class AdapterCommandAttribute : Attribute
    {
        public AdapterCommandAttribute(string description = null) => Description = description;

        /// <summary>What the command does, in one line, for whoever is deciding whether to run it.</summary>
        public string Description { get; set; }
    }
}
