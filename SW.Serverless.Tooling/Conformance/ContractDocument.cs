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

        /// <summary>The contracts the kit knows by name, carried with it.</summary>
        public static ContractDocument Known(string name, int version)
        {
            // Found by file name: the folder in a resource's name is written with whichever
            // separator the machine that built it uses.
            var file = $"{name}-adapter-contract.v{version}.json";
            var resource = typeof(ContractDocument).Assembly.GetManifestResourceNames()
                .FirstOrDefault(r => r.StartsWith("SW.Serverless.Tooling.Contracts.", StringComparison.Ordinal) &&
                                     r.EndsWith(file, StringComparison.Ordinal));
            if (resource == null) return null;

            var prefix = resource[..^file.Length];
            return new ContractDocument(JObject.Parse(ReadResource(resource)), sibling => ReadResource(prefix + sibling));
        }

        /// <summary>A contract from a file, with its schemas beside it.</summary>
        public static ContractDocument FromFile(string path)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            return new ContractDocument(JObject.Parse(File.ReadAllText(path)),
                file => File.ReadAllText(Path.Combine(directory, file)));
        }

        static string ReadResource(string name)
        {
            using var stream = typeof(ContractDocument).Assembly.GetManifestResourceStream(name)
                               ?? throw new FileNotFoundException(name);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public record ContractMethod(string Name, string Input, string Output, IReadOnlyList<JToken> Examples, bool Destructive);
}
