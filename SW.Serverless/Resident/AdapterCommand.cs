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

        /// <summary>The argument's JSON Schema, from an SDK in any language. Empty when not given.</summary>
        public string InputSchema { get; set; }

        /// <summary>The result's JSON Schema, from an SDK in any language. Empty when not given.</summary>
        public string OutputSchema { get; set; }

        /// <summary>False for a command returning Task rather than Task&lt;T&gt;.</summary>
        public bool ReturnsValue { get; set; }

        /// <summary>From [AdapterCommand] on the method, when the author set one.</summary>
        public string Description { get; set; }

        public bool TakesArgument => !string.IsNullOrEmpty(ParameterType);

        public override string ToString() =>
            $"{Name}({ParameterType}){(ReturnsValue ? "" : " -> void")}";
    }

    /// <summary>One setting an adapter says it reads, in any language.</summary>
    public class AdapterSetting
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; }

        /// <summary>Masked wherever it's shown, never logged.</summary>
        public bool Secret { get; set; }

        /// <summary>Empty when the setting has no default.</summary>
        public string DefaultValue { get; set; }

        /// <summary>text, multiline, number, boolean, select or json.</summary>
        public string Type { get; set; }
    }
}
