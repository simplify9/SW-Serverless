using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace SW.Serverless.Tooling.Conformance
{
    /// <summary>
    /// A contract an adapter can implement, as its machine-readable JSON: the kinds, the methods a
    /// host calls on each, their payload types and schemas, and example inputs. Nothing here knows
    /// any particular contract; the kit checks an adapter against whichever one it declares.
    /// </summary>
    public class ContractDocument
    {
        readonly JObject json;
        readonly Func<string, string> readSibling;

        ContractDocument(JObject json, Func<string, string> readSibling)
        {
            this.json = json;
            this.readSibling = readSibling;
        }

        public string Name => (string)json["contract"];
        public int Version => (int?)json["version"] ?? 0;

        public IEnumerable<string> Kinds => ((JObject)json["kinds"])?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>();

        public IReadOnlyList<ContractMethod> MethodsOf(string kind) =>
            json["kinds"]?[kind]?["methods"]?.Select(m => new ContractMethod(
                    (string)m["name"],
                    (string)m["input"],
                    (string)m["output"],
                    m["examples"]?.ToList() ?? new List<JToken>(),
                    (bool?)m["destructive"] ?? false))
                .ToList()
            ?? new List<ContractMethod>();

        public bool IsSession(string kind) => json["kinds"]?[kind]?["session"] != null;

        /// <summary>Whether a type travels as raw text rather than JSON.</summary>
        public bool IsText(string type) => (string)json["types"]?[type]?["encoding"] == "string";

        /// <summary>The JSON Schema of a payload type, or null for one with none (raw text).</summary>
        public async Task<JsonSchema> SchemaOfAsync(string type)
        {
            var declared = json["types"]?[type]?["schema"];
            return declared switch
            {
                null => null,
                JValue file => await JsonSchema.FromJsonAsync(readSibling((string)file)),
                JObject inline => await JsonSchema.FromJsonAsync(inline.ToString()),
                _ => null,
            };
        }

        static readonly List<ContractDocument> registered = new();

        /// <summary>
        /// Makes a contract known by name, so the kit checks every adapter that declares it without
        /// being handed it each time — what an application's own CLI does with its contract.
        /// </summary>
        public static void Register(ContractDocument contract)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            lock (registered)
            {
                registered.RemoveAll(c => string.Equals(c.Name, contract.Name, StringComparison.OrdinalIgnoreCase) && c.Version == contract.Version);
                registered.Add(contract);
            }
        }

        /// <summary>A registered contract, or null. The kit carries none of its own.</summary>
        public static ContractDocument Known(string name, int version)
        {
            lock (registered)
                return registered.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) && c.Version == version);
        }

        /// <summary>
        /// A contract from its JSON, with <paramref name="readSibling"/> reading the schema files it
        /// names — from embedded resources, say.
        /// </summary>
        public static ContractDocument FromJson(string json, Func<string, string> readSibling) =>
            new(JObject.Parse(json), readSibling ?? (file => throw new FileNotFoundException($"The contract names {file}, and nothing was given to read it.")));

        /// <summary>A contract from a file, with its schemas beside it.</summary>
        public static ContractDocument FromFile(string path)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            return new ContractDocument(JObject.Parse(File.ReadAllText(path)),
                file => File.ReadAllText(Path.Combine(directory, file)));
        }
    }

    public record ContractMethod(string Name, string Input, string Output, IReadOnlyList<JToken> Examples, bool Destructive);
}
