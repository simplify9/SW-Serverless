using System;

namespace SW.Serverless.Resident
{
    /// <summary>
    /// One command an adapter exposes, in enough detail to call it without reading its source.
    ///
    /// The names alone have always been available. What was missing is the shape — whether a
    /// command takes an argument, what that argument looks like, and whether anything comes back —
    /// which is the difference between knowing an adapter has a "Discover" method and being able
    /// to offer it to an operator.
    /// </summary>
    public class AdapterCommand
    {
        public string Name { get; set; }

        /// <summary>The argument's type name, or empty when the command takes none.</summary>
        public string ParameterType { get; set; }

        /// <summary>
        /// A complex argument's public properties as a JSON object of name to type, so a caller
        /// can build a form for a command it has never seen. Empty for a primitive or absent
        /// argument, where <see cref="ParameterType"/> already says everything.
        /// </summary>
        public string ParameterSchema { get; set; }

        /// <summary>False for a command returning Task rather than Task&lt;T&gt;.</summary>
        public bool ReturnsValue { get; set; }

        /// <summary>From [AdapterCommand] on the method, when the author set one.</summary>
        public string Description { get; set; }

        public bool TakesArgument => !string.IsNullOrEmpty(ParameterType);

        public override string ToString() =>
            $"{Name}({ParameterType}){(ReturnsValue ? "" : " -> void")}";
    }
}
