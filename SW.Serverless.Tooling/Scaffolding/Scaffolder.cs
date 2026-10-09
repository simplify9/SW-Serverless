using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SW.Serverless.Tooling.Scaffolding
{
    public class ScaffoldRequest
    {
        /// <summary>The adapter's name, e.g. AcmeOrders: the folder, the project and the class names.</summary>
        public string Name { get; set; }

        /// <summary>Its id in storage; derived from the name when not given.</summary>
        public string Id { get; set; }

        public string Language { get; set; } = "dotnet";

        /// <summary>handler, mapper, validator or receiver.</summary>
        public string Kind { get; set; } = "handler";

        /// <summary>The folder the project folder is made in.</summary>
        public string ParentDirectory { get; set; } = ".";
    }

    public class ScaffoldResult
    {
        public string ProjectDirectory { get; set; }
        public List<string> Files { get; } = new();
        public List<string> Problems { get; } = new();
        public bool Succeeded => Problems.Count == 0;
    }

    /// <summary>
    /// What serverless init writes: a working adapter of one kind for the Bitween contract, ready to
    /// build, test and publish. Also what a code editor starts a new adapter from.
    /// </summary>
    public static class Scaffolder
    {
        /// <summary>The SDK the templates reference; the first with --describe and the Bitween contract types.</summary>
        public const string SdkPackageVersion = "10.1.0";

        /// <summary>
        /// The Bitween contract package the templates reference: the first Bitween release that
        /// publishes it. NuGet reads it as a minimum, so it resolves to the first one there is.
        /// </summary>
        public const string BitweenAdaptersPackageVersion = "10.0.59";

        public static readonly IReadOnlyList<string> Kinds = new[] { "handler", "mapper", "validator", "receiver" };
        public static readonly IReadOnlyList<string> Languages = new[] { "dotnet", "python", "node", "typescript" };

        /// <summary>The Python SDK the templates name; serverless build vendors the copy it carries.</summary>
        public const string PythonSdkVersion = "10.1.0";

        /// <summary>The Node SDK the templates name; serverless build vendors the copy it carries.</summary>
        public const string NodeSdkVersion = "10.1.0";

        public static ScaffoldResult Scaffold(ScaffoldRequest request)
        {
            var result = new ScaffoldResult();
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]*$"))
                result.Problems.Add($"'{request.Name}' isn't a name a project and a class can take: letters, digits and _, starting with a letter");
            if (!Kinds.Contains(request.Kind))
                result.Problems.Add($"'{request.Kind}' isn't a kind: use {string.Join(", ", Kinds)}");
            if (!Languages.Contains(request.Language))
                result.Problems.Add($"a '{request.Language}' template arrives with that language's SDK; this release scaffolds {string.Join(", ", Languages)}");

            var id = string.IsNullOrWhiteSpace(request.Id) ? IdFrom(name ?? "") : request.Id.Trim();
            if (!InstallerLogic.IsValidAdapterId(id))
                result.Problems.Add($"'{id}' isn't an adapter id: lowercase letters, digits, '.', '_' and '-'");

            var directory = Path.GetFullPath(Path.Combine(request.ParentDirectory ?? ".", name ?? ""));
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                result.Problems.Add($"{directory} already exists and isn't empty");
            if (!result.Succeeded) return result;

            Directory.CreateDirectory(directory);
            result.ProjectDirectory = directory;
            var files = request.Language switch
            {
                "python" => PythonFiles(name, id, request.Kind),
                "node" => NodeFiles(name, id, request.Kind, typeScript: false),
                "typescript" => NodeFiles(name, id, request.Kind, typeScript: true),
                _ => DotnetFiles(name, id, request.Kind),
            };
            foreach (var (file, content) in files)
            {
                var path = Path.Combine(directory, file);
                // Ending in a newline, as text files should: a line appended later stays its own line.
                var text = content.Replace("\r\n", "\n");
                File.WriteAllText(path, text.EndsWith('\n') ? text : text + "\n");
                result.Files.Add(file);
            }
            return result;
        }

        /// <summary>AcmeOrders → acme.orders</summary>
        public static string IdFrom(string name) =>
            Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", ".").Replace('_', '.').ToLowerInvariant();

        static IEnumerable<(string File, string Content)> DotnetFiles(string name, string id, string kind)
        {
            yield return ($"{name}.csproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk">

                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <!-- Locked, so the source this package carries rebuilds with exactly these dependencies. -->
                    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
                  </PropertyGroup>

                  <ItemGroup>
                    <PackageReference Include="SimplyWorks.Serverless.Sdk" Version="{{SdkPackageVersion}}" />
                    <PackageReference Include="SimplyWorks.Bitween.Adapters" Version="{{BitweenAdaptersPackageVersion}}" />
                  </ItemGroup>

                </Project>
                """);

            yield return ("adapter.json", $$"""
                {
                  "id": "{{id}}",
                  "version": "0.1.0",
                  "displayName": "{{Spaced(name)}}",
                  "summary": "What this {{kind}} does, in one sentence, for the adapter list.",
                  "lifecycle": "classic"
                }
                """);

            yield return ("Program.cs", Program(name, kind));

            yield return ("settings.example.json", """
                {
                  "BaseUrl": "https://partner.example.test",
                  "ApiKey": "put a test key here, and keep this file out of version control once it holds one"
                }
                """);

            yield return (".gitignore", """
                bin/
                obj/
                settings.json
                """);

            yield return ("README.md", $$"""
                # {{Spaced(name)}}

                A Bitween {{kind}} adapter.

                ```sh
                serverless build                                   # builds bin/serverless/{{id}}-0.1.0.zip
                cp settings.example.json settings.json             # then fill in real values
                serverless test --settings settings.json           # checks it against the Bitween contract
                serverless publish bin/serverless/{{id}}-0.1.0.zip  # with your storage flags
                ```

                Settings are declared in code with `Runner.Expect`; `serverless build` writes them into
                the manifest Bitween reads.
                """);
        }

        static string Program(string name, string kind) => kind switch
        {
            "receiver" => $$"""
                using SW.Bitween.Adapters;
                using SW.Serverless.Sdk;

                namespace {{name}};

                /// <summary>Fetches files on a schedule. Bitween calls Initialize, ListFiles, then GetFile and DeleteFile for each file, then Finalize.</summary>
                [AdapterKind("receiver")]
                [AdapterContract("bitween", 1)]
                public class Receiver : IBitweenReceiver
                {
                    public Receiver()
                    {
                        // Declare settings here; read them in the methods, never in the constructor.
                        Runner.Expect("BaseUrl", "https://partner.example.test", description: "Where files are fetched from.");
                        Runner.Expect("ApiKey", isPrivate: true, description: "The partner's key.");
                    }

                    public Task Initialize() => Task.CompletedTask;

                    public Task<IEnumerable<string>> ListFiles() =>
                        Task.FromResult<IEnumerable<string>>(new[] { "example-1" });

                    public Task<ExchangeFile> GetFile(string fileId) =>
                        Task.FromResult(new ExchangeFile("{\"id\":\"" + fileId + "\"}", fileId + ".json"));

                    public Task DeleteFile(string fileId) => Task.CompletedTask;

                    public Task Finalize() => Task.CompletedTask;
                }

                static class Program
                {
                    static Task Main() => Runner.Run(new Receiver());
                }
                """,
            "validator" => $$"""
                using SW.Bitween.Adapters;
                using SW.Serverless.Sdk;

                namespace {{name}};

                /// <summary>Checks a message before Bitween accepts it.</summary>
                [AdapterKind("validator")]
                [AdapterContract("bitween", 1)]
                public class Validator : IBitweenValidator
                {
                    public Validator()
                    {
                        Runner.Expect("MaxBytes", "1000000", description: "The largest message accepted.");
                    }

                    public Task<ValidationResult> Validate(ExchangeFile file)
                    {
                        var result = new ValidationResult();
                        if (file.Data.Length > Runner.StartupValueOf<int>("MaxBytes"))
                            result.AddError("Data", "The message is larger than allowed.");
                        return Task.FromResult(result);
                    }
                }

                static class Program
                {
                    static Task Main() => Runner.Run(new Validator());
                }
                """,
            var handlerOrMapper => $$"""
                using SW.Bitween.Adapters;
                using SW.Serverless.Sdk;

                namespace {{name}};

                /// <summary>{{(handlerOrMapper == "mapper" ? "Maps a message into the shape the next step expects." : "Delivers a message and returns the partner's response.")}}</summary>
                [AdapterKind("{{handlerOrMapper}}")]
                [AdapterContract("bitween", 1)]
                public class {{(handlerOrMapper == "mapper" ? "Mapper : IBitweenMapper" : "Handler : IBitweenHandler")}}
                {
                    public {{(handlerOrMapper == "mapper" ? "Mapper" : "Handler")}}()
                    {
                        // Declare settings here; read them in the methods, never in the constructor.
                        Runner.Expect("BaseUrl", "https://partner.example.test", description: "Where messages go.");
                        Runner.Expect("ApiKey", isPrivate: true, description: "The partner's key.");
                    }

                    public Task<ExchangeFile> Handle(ExchangeFile file)
                    {
                        // {{(handlerOrMapper == "mapper" ? "Return the message in its new shape." : "Send file.Data to the partner. A rejection is returned with BadData set, not thrown.")}}
                        return Task.FromResult(new ExchangeFile(file.Data, file.Filename));
                    }
                }

                static class Program
                {
                    static Task Main() => Runner.Run(new {{(handlerOrMapper == "mapper" ? "Mapper" : "Handler")}}());
                }
                """,
        };

        static IEnumerable<(string File, string Content)> PythonFiles(string name, string id, string kind)
        {
            yield return ("adapter.json", $$"""
                {
                  "id": "{{id}}",
                  "version": "0.1.0",
                  "displayName": "{{Spaced(name)}}",
                  "summary": "What this {{kind}} does, in one sentence, for the adapter list.",
                  "runtime": "python",
                  "entry": "main.py"
                }
                """);

            yield return ("main.py", PythonMain(name, kind));

            yield return ("requirements.txt", $$"""
                # What the adapter imports, pinned, one per line; serverless build vendors them into the
                # package. The two SDKs are vendored by serverless build itself: they're listed here for
                # your editor and for running the tests outside a build.
                simplyworks-serverless=={{PythonSdkVersion}}
                simplyworks-bitween>={{BitweenAdaptersPackageVersion}}
                """);

            yield return ("settings.example.json", """
                {
                  "BaseUrl": "https://partner.example.test",
                  "ApiKey": "put a test key here, and keep this file out of version control once it holds one"
                }
                """);

            yield return (".gitignore", """
                __pycache__/
                .venv/
                bin/
                settings.json
                """);

            yield return ("README.md", $$"""
                # {{Spaced(name)}}

                A Bitween {{kind}} adapter in Python (3.12 or later).

                ```sh
                serverless build                                   # builds bin/serverless/{{id}}-0.1.0.zip
                cp settings.example.json settings.json             # then fill in real values
                serverless test --settings settings.json           # checks it against the Bitween contract
                serverless publish bin/serverless/{{id}}-0.1.0.zip  # with your storage flags
                ```

                Settings are declared in code with `sw.expect`; `serverless build` writes them into the
                manifest Bitween reads. Dependencies go in `requirements.txt`, pinned.
                """);
        }

        static string PythonMain(string name, string kind) => kind switch
        {
            "receiver" => $$""""
                import simplyworks_serverless as sw
                from simplyworks_bitween import ExchangeFile, Receiver


                class {{name}}(Receiver):
                    """Fetches files on a schedule. Bitween calls initialize, list_files, then get_file and
                    delete_file for each file, then finalize."""

                    def __init__(self):
                        # Declare settings here; read them in the methods with sw.value_of.
                        sw.expect("BaseUrl", "https://partner.example.test", description="Where files are fetched from.")
                        sw.expect("ApiKey", secret=True, description="The partner's key.")

                    def list_files(self) -> list[str]:
                        return ["example-1"]

                    def get_file(self, file_id: str) -> ExchangeFile:
                        return ExchangeFile(data='{"id": "%s"}' % file_id, filename=file_id + ".json")

                    def delete_file(self, file_id: str) -> None:
                        pass


                if __name__ == "__main__":
                    sw.run({{name}})
                """",
            "validator" => $$""""
                import simplyworks_serverless as sw
                from simplyworks_bitween import ExchangeFile, ValidationResult, Validator


                class {{name}}(Validator):
                    """Checks a message before Bitween accepts it."""

                    def __init__(self):
                        sw.expect("MaxBytes", "1000000", type="number", description="The largest message accepted.")

                    def validate(self, file: ExchangeFile) -> ValidationResult:
                        result = ValidationResult()
                        if len(file.data) > int(sw.value_of("MaxBytes")):
                            result.add("Data", "The message is larger than allowed.")
                        return result


                if __name__ == "__main__":
                    sw.run({{name}})
                """",
            "mapper" => $$""""
                import simplyworks_serverless as sw
                from simplyworks_bitween import ExchangeFile, Mapper


                class {{name}}(Mapper):
                    """Maps a message into the shape the next step expects."""

                    def map(self, file: ExchangeFile) -> ExchangeFile:
                        # Return the message in its new shape.
                        return ExchangeFile(data=file.data, filename=file.filename)


                if __name__ == "__main__":
                    sw.run({{name}})
                """",
            _ => $$""""
                import simplyworks_serverless as sw
                from simplyworks_bitween import ExchangeFile, Handler


                class {{name}}(Handler):
                    """Delivers a message and returns the partner's response."""

                    def __init__(self):
                        # Declare settings here; read them in the methods with sw.value_of.
                        sw.expect("BaseUrl", "https://partner.example.test", description="Where messages go.")
                        sw.expect("ApiKey", secret=True, description="The partner's key.")

                    def handle(self, file: ExchangeFile) -> ExchangeFile:
                        # Send file.data to the partner. A rejection is returned with bad_data=True, not raised.
                        return ExchangeFile(data=file.data, filename=file.filename)


                if __name__ == "__main__":
                    sw.run({{name}})
                """",
        };

        static IEnumerable<(string File, string Content)> NodeFiles(string name, string id, string kind, bool typeScript)
        {
            var entry = typeScript ? "main.ts" : "main.js";
            yield return ("adapter.json", $$"""
                {
                  "id": "{{id}}",
                  "version": "0.1.0",
                  "displayName": "{{Spaced(name)}}",
                  "summary": "What this {{kind}} does, in one sentence, for the adapter list.",
                  "runtime": "node",
                  "entry": "{{entry}}"
                }
                """);

            yield return (entry, NodeMain(name, kind, typeScript));

            // The SDKs are named for the editor and for running outside a build; serverless build
            // vendors the copies it carries. Other dependencies go here too, and are installed into
            // the package.
            var package = new System.Text.Json.Nodes.JsonObject { ["name"] = id, ["private"] = true };
            if (typeScript) package["type"] = "module";
            package["engines"] = new System.Text.Json.Nodes.JsonObject { ["node"] = ">=22" };
            package["dependencies"] = new System.Text.Json.Nodes.JsonObject
            {
                ["@simplyworks/serverless"] = NodeSdkVersion,
                ["@simplyworks/bitween"] = ">=" + BitweenAdaptersPackageVersion,
            };
            yield return ("package.json", package.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            if (typeScript)
                yield return ("tsconfig.json", """
                    {
                      // For your editor and tsc --noEmit. serverless build doesn't compile: Node strips the
                      // types, so only syntax that strips cleanly is allowed (no enums or namespaces).
                      "compilerOptions": {
                        "target": "es2023",
                        "module": "nodenext",
                        "moduleResolution": "nodenext",
                        "strict": true,
                        "noEmit": true,
                        "erasableSyntaxOnly": true,
                        "verbatimModuleSyntax": true,
                        "allowImportingTsExtensions": true
                      }
                    }
                    """);

            yield return ("settings.example.json", """
                {
                  "BaseUrl": "https://partner.example.test",
                  "ApiKey": "put a test key here, and keep this file out of version control once it holds one"
                }
                """);

            yield return (".gitignore", """
                node_modules/
                bin/
                settings.json
                """);

            yield return ("README.md", $$"""
                # {{Spaced(name)}}

                A Bitween {{kind}} adapter in {{(typeScript ? "TypeScript" : "JavaScript")}}, for Node 22 or later.

                ```sh
                serverless build                                   # builds bin/serverless/{{id}}-0.1.0.zip
                cp settings.example.json settings.json             # then fill in real values
                serverless test --settings settings.json           # checks it against the Bitween contract
                serverless publish bin/serverless/{{id}}-0.1.0.zip  # with your storage flags
                ```

                Settings are declared in code with `expect`; `serverless build` writes them into the
                manifest Bitween reads.{{(typeScript ? " The build strips the types with Node itself, so no compiler is needed; `tsc --noEmit` checks them." : "")}}
                """);
        }

        static string NodeMain(string name, string kind, bool ts)
        {
            var header = ts
                ? $$"""
                    import { expect, run, valueOf } from "@simplyworks/serverless";
                    import { ExchangeFile, {{Pascal(kind)}}{{(kind == "validator" ? ", ValidationResult" : "")}} } from "@simplyworks/bitween";
                    """
                : $$"""
                    const { expect, run, valueOf } = require("@simplyworks/serverless");
                    const { ExchangeFile, {{Pascal(kind)}}{{(kind == "validator" ? ", ValidationResult" : "")}} } = require("@simplyworks/bitween");
                    """;
            string T(string type) => ts ? type : "";
            var body = kind switch
            {
                "receiver" => $$"""
                    /** Fetches files on a schedule. Bitween calls initialize, listFiles, then getFile and deleteFile for each file, then finalize. */
                    class {{name}} extends Receiver {
                      constructor() {
                        super();
                        // Declare settings here; read them in the methods with valueOf.
                        expect("BaseUrl", { default: "https://partner.example.test", description: "Where files are fetched from." });
                        expect("ApiKey", { secret: true, description: "The partner's key." });
                      }

                      listFiles(){{T(": string[]")}} {
                        return ["example-1"];
                      }

                      getFile(fileId{{T(": string")}}){{T(": ExchangeFile")}} {
                        return new ExchangeFile({ data: JSON.stringify({ id: fileId }), filename: `${fileId}.json` });
                      }

                      deleteFile(fileId{{T(": string")}}){{T(": void")}} {}
                    }
                    """,
                "validator" => $$"""
                    /** Checks a message before Bitween accepts it. */
                    class {{name}} extends Validator {
                      constructor() {
                        super();
                        expect("MaxBytes", { default: "1000000", type: "number", description: "The largest message accepted." });
                      }

                      validate(file{{T(": ExchangeFile")}}){{T(": ValidationResult")}} {
                        const result = new ValidationResult();
                        if (file.data.length > Number(valueOf("MaxBytes"))) result.add("Data", "The message is larger than allowed.");
                        return result;
                      }
                    }
                    """,
                "mapper" => $$"""
                    /** Maps a message into the shape the next step expects. */
                    class {{name}} extends Mapper {
                      map(file{{T(": ExchangeFile")}}){{T(": ExchangeFile")}} {
                        // Return the message in its new shape.
                        return new ExchangeFile({ data: file.data, filename: file.filename });
                      }
                    }
                    """,
                _ => $$"""
                    /** Delivers a message and returns the partner's response. */
                    class {{name}} extends Handler {
                      constructor() {
                        super();
                        // Declare settings here; read them in the methods with valueOf.
                        expect("BaseUrl", { default: "https://partner.example.test", description: "Where messages go." });
                        expect("ApiKey", { secret: true, description: "The partner's key." });
                      }

                      handle(file{{T(": ExchangeFile")}}){{T(": ExchangeFile")}} {
                        // Send file.data to valueOf("BaseUrl"). A rejection is returned with badData: true, not thrown.
                        return new ExchangeFile({ data: file.data, filename: file.filename });
                      }
                    }
                    """,
            };
            // valueOf is used by every template but the mapper; keep the import list honest there.
            if (kind == "mapper") header = header.Replace("expect, run, valueOf", "run");
            else if (kind == "receiver") header = header.Replace("expect, run, valueOf", "expect, run");
            return header + "\n\n" + body + "\n\nrun(" + name + ");\n";
        }

        static string Pascal(string kind) => char.ToUpperInvariant(kind[0]) + kind[1..];

        static string Spaced(string name) => Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
    }
}
