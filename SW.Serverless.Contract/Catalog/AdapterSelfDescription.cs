using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SW.Serverless.Contract.Catalog
{
    /// <summary>
    /// What an adapter says about itself when started with <see cref="Flag"/>: printed as one JSON
    /// document on stdout, after which it exits. Every SDK, in every language, produces this, so the
    /// tools learn an adapter's settings, commands, kinds and contracts by running it — not by
    /// reading .NET assemblies — and write them into its manifest.
    /// </summary>
    public class AdapterSelfDescription
    {
        public const string Flag = "--describe";
        public const int CurrentDescribeVersion = 1;

        public int DescribeVersion { get; set; } = CurrentDescribeVersion;

        /// <summary>dotnet, python, node, go…</summary>
        public string SdkLanguage { get; set; }
        public string SdkVersion { get; set; }

        /// <summary>classic or resident: how the adapter's entry point runs it.</summary>
        public string Lifecycle { get; set; }

        /// <summary>The protocol versions it speaks: 1 is the classic text protocol, 2 is gRPC.</summary>
        public AdapterProtocolRange Protocol { get; set; }

        public List<DescribedSetting> Settings { get; set; } = new();
        public List<DescribedCommand> Commands { get; set; } = new();
        public List<string> Kinds { get; set; } = new();
        public Dictionary<string, int> Contracts { get; set; } = new();

        /// <summary>Anything that kept the description from being complete, e.g. a handler that couldn't be built without its settings.</summary>
        public List<string> Warnings { get; set; } = new();

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extensions { get; set; }

        public static AdapterSelfDescription Parse(string json) =>
            JsonSerializer.Deserialize<AdapterSelfDescription>(json, AdapterManifest.JsonOptions)
            ?? throw new JsonException("The adapter's description is empty.");

        public string ToJson() => JsonSerializer.Serialize(this, AdapterManifest.JsonOptions);
    }

    public class DescribedSetting
    {
        public string Name { get; set; }
        public string Description { get; set; }

        /// <summary>text, multiline, number, boolean, select or json, as manifest properties are typed.</summary>
        public string Type { get; set; } = AdapterProperty.TextType;

        public bool Required { get; set; }
        public bool Secret { get; set; }
        public string Default { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extensions { get; set; }
    }

    public class DescribedCommand
    {
        public string Name { get; set; }
        public string Description { get; set; }

        /// <summary>JSON Schema of the argument; null when it takes none.</summary>
        public JsonElement? InputSchema { get; set; }

        /// <summary>JSON Schema of the result; null when it returns nothing.</summary>
        public JsonElement? OutputSchema { get; set; }

        public bool ReturnsValue { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extensions { get; set; }
    }
}
