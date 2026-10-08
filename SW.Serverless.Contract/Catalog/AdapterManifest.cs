using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SW.Serverless.Contract.Catalog
{
    /// <summary>
    /// What an adapter package says about itself: how to run it, what it needs configured, and how
    /// to present it in a catalog or marketplace. Carried inside the package as
    /// <see cref="FileName"/>, so whatever verifies the package verifies this too — unlike storage
    /// metadata, which can be changed without touching the package.
    /// </summary>
    /// <remarks>
    /// Every field is optional on read. A package without a manifest, or with one written by an
    /// older installer, still runs exactly as before; fields this version does not know are kept in
    /// <see cref="Extensions"/> and written back, so a newer manifest survives an older tool.
    /// </remarks>
    public class AdapterManifest
    {
        public const string FileName = "adapter.json";
        public const int CurrentManifestVersion = 1;

        public const string ClassicLifecycle = "classic";
        public const string ResidentLifecycle = "resident";
        public const string DotnetRuntime = "dotnet";

        public int ManifestVersion { get; set; } = CurrentManifestVersion;

        // ---------------------------------------------------------------- identity

        /// <summary>The adapter id: lower-case letters, digits, '.', '_' and '-'.</summary>
        public string Id { get; set; }

        /// <summary>Semantic version of this build, e.g. 1.4.0. Empty for an unversioned upload.</summary>
        public string Version { get; set; }

        // ---------------------------------------------------------------- presentation

        public string DisplayName { get; set; }

        /// <summary>One line, for a list or a card.</summary>
        public string Summary { get; set; }

        /// <summary>Longer description, Markdown.</summary>
        public string Description { get; set; }

        public AdapterPublisher Publisher { get; set; }
        public string License { get; set; }
        public string Homepage { get; set; }
        public string Repository { get; set; }

        /// <summary>An image inside the package — PNG, JPEG or SVG — relative to its root.</summary>
        public string Icon { get; set; }

        public List<string> Tags { get; set; } = new();
        public List<string> Categories { get; set; } = new();

        /// <summary>What changed in this version, Markdown.</summary>
        public string ReleaseNotes { get; set; }

        // ---------------------------------------------------------------- running

        /// <summary>What the adapter does for its host: handler, mapper, receiver, validator, datasource…</summary>
        public List<string> Kinds { get; set; } = new();

        /// <summary>
        /// Which launcher runs it. A name the host maps to a launcher it trusts — never a path —
        /// so a package cannot ask the host to start an arbitrary program. "dotnet" today.
        /// </summary>
        public string Runtime { get; set; } = DotnetRuntime;

        /// <summary>The language it is written in, for listing: csharp, fsharp, python…</summary>
        public string Language { get; set; }

        /// <summary>The file the runtime starts, relative to the package root.</summary>
        public string Entry { get; set; }

        /// <summary><see cref="ClassicLifecycle"/> or <see cref="ResidentLifecycle"/>.</summary>
        public string Lifecycle { get; set; } = ClassicLifecycle;

        /// <summary>The resident protocol versions it speaks. Absent for classic adapters.</summary>
        public AdapterProtocolRange Protocol { get; set; }

        /// <summary>The SDK version it was built against, as the installer found it.</summary>
        public string SdkVersion { get; set; }

        public AdapterCompatibility Compatibility { get; set; }

        /// <summary>What has to be configured for it to run, and how a form should ask for it.</summary>
        public List<AdapterProperty> Properties { get; set; } = new();

        public DateTimeOffset? PublishedOn { get; set; }

        /// <summary>Fields written by a newer tool, kept so they are not lost on a round trip.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extensions { get; set; }

        // ---------------------------------------------------------------- serialisation

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static AdapterManifest Parse(string json) =>
            JsonSerializer.Deserialize<AdapterManifest>(json, JsonOptions)
            ?? throw new JsonException("The adapter manifest is empty.");

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        public bool IsResident =>
            string.Equals(Lifecycle, ResidentLifecycle, StringComparison.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- validation

        static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9._-]*$");
        static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");
        static readonly Regex PropertyNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_.:-]*$");

        /// <summary>What is wrong with this manifest, empty when nothing is.</summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();

            if (ManifestVersion < 1)
                problems.Add("manifestVersion must be 1 or more.");
            if (!string.IsNullOrEmpty(Id) && !IdPattern.IsMatch(Id))
                problems.Add($"id '{Id}' must be lower-case letters, digits, '.', '_' and '-'.");
            if (!string.IsNullOrEmpty(Version) && !VersionPattern.IsMatch(Version))
                problems.Add($"version '{Version}' is not a semantic version such as 1.4.0.");
            if (!string.IsNullOrEmpty(Entry) && !IsPackagePath(Entry))
                problems.Add($"entry '{Entry}' must be a path inside the package.");
            if (!string.IsNullOrEmpty(Icon) && !IsPackagePath(Icon))
                problems.Add($"icon '{Icon}' must be a path inside the package.");
            if (!string.IsNullOrEmpty(Lifecycle) && Lifecycle is not (ClassicLifecycle or ResidentLifecycle))
                problems.Add($"lifecycle '{Lifecycle}' must be '{ClassicLifecycle}' or '{ResidentLifecycle}'.");
            if (Protocol != null && Protocol.Min > Protocol.Max)
                problems.Add("protocol.min is higher than protocol.max.");
            if (Compatibility?.MinHostVersion is { Length: > 0 } host && !VersionPattern.IsMatch(host) &&
                !System.Version.TryParse(host, out _))
                problems.Add($"compatibility.minHostVersion '{host}' is not a version.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in Properties ?? new List<AdapterProperty>())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || !PropertyNamePattern.IsMatch(property.Name))
                    problems.Add($"property name '{property.Name}' is not valid.");
                else if (!seen.Add(property.Name))
                    problems.Add($"property '{property.Name}' is declared twice.");
                if (!string.IsNullOrEmpty(property.Type) && !AdapterProperty.KnownTypes.Contains(property.Type))
                    problems.Add($"property '{property.Name}' has unknown type '{property.Type}'.");
                if (property.Type == AdapterProperty.SelectType && (property.Options == null || property.Options.Count == 0))
                    problems.Add($"property '{property.Name}' is a select with no options.");
            }

            return problems;
        }

        /// <summary>Relative, forward-slashed, and never climbing out with "..".</summary>
        public static bool IsPackagePath(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            !path.StartsWith("/") && !path.StartsWith("\\") && !path.Contains(':') &&
            !path.Replace('\\', '/').Split('/').Any(segment => segment == "..");
    }

    public class AdapterPublisher
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Email { get; set; }
    }

    public class AdapterProtocolRange
    {
        public int Min { get; set; }
        public int Max { get; set; }
    }

    /// <summary>The oldest host each side may run on. Absent means no constraint.</summary>
    public class AdapterCompatibility
    {
        /// <summary>The lowest SW.Serverless host that may run it. The host refuses to install it below.</summary>
        public string MinHostVersion { get; set; }

        /// <summary>The lowest Bitween that may use it. Bitween warns and refuses to bind it below.</summary>
        public string MinBitweenVersion { get; set; }
    }

    /// <summary>One setting the adapter needs, with what a form needs to ask for it.</summary>
    public class AdapterProperty
    {
        public const string TextType = "text";
        public const string MultilineType = "multiline";
        public const string NumberType = "number";
        public const string BooleanType = "boolean";
        public const string SelectType = "select";
        public const string JsonType = "json";

        public static readonly IReadOnlyCollection<string> KnownTypes =
            new[] { TextType, MultilineType, NumberType, BooleanType, SelectType, JsonType };

        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }

        /// <summary>One of <see cref="KnownTypes"/>; text when absent.</summary>
        public string Type { get; set; } = TextType;

        public bool Required { get; set; }

        /// <summary>A password, key or token: masked on screen and never echoed back.</summary>
        public bool Secret { get; set; }

        public string Default { get; set; }

        /// <summary>The choices, for <see cref="SelectType"/>.</summary>
        public List<string> Options { get; set; }

        /// <summary>A heading to gather related properties under, e.g. "Connection".</summary>
        public string Group { get; set; }
    }
}
