using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SW.Serverless.Tooling.Scaffolding
{
    public class ScaffoldRequest
    {
        /// <summary>The adapter's name, e.g. AcmeOrders: the folder, the project and the class names.</summary>
        public string Name { get; set; }

        /// <summary>Its id in storage; derived from the name when not given.</summary>
        public string Id { get; set; }

        /// <summary>dotnet, python, node (JavaScript) or typescript.</summary>
        public string Language { get; set; } = "dotnet";

        /// <summary>
        /// What a template set means by it — an application's own kinds, say. The templates here have
        /// none, and ignore it.
        /// </summary>
        public string Kind { get; set; }

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
    /// What sw-serverless init writes: a working adapter with two settings and two commands, in any of
    /// the SDK's languages, ready to build, test, run and publish. An application with adapters of
    /// its own — kinds, a contract — passes its own templates to <see cref="Scaffold(ScaffoldRequest, Templates)"/>
    /// and keeps the same checks and layout.
    /// </summary>
    public static class Scaffolder
    {
        /// <summary>The .NET SDK the templates reference.</summary>
        public const string SdkPackageVersion = "10.1.0";

        /// <summary>The Python SDK the templates name; the build vendors the copy it carries.</summary>
        public const string PythonSdkVersion = "10.2.2";

        /// <summary>The Node SDK the templates name; the build vendors the copy it carries.</summary>
        public const string NodeSdkVersion = "10.2.2";

        public static readonly IReadOnlyList<string> Languages = new[] { "dotnet", "python", "node", "typescript" };

        /// <summary>A template set: the files of a new adapter, by path, for its name, id, language and kind.</summary>
        public delegate IEnumerable<(string File, string Content)> Templates(string name, string id, string language, string kind);

        /// <summary>Writes the generic adapter.</summary>
        public static ScaffoldResult Scaffold(ScaffoldRequest request) => Scaffold(request, Generic);

        /// <summary>
        /// Writes a new adapter from <paramref name="templates"/>, after the checks every one needs:
        /// a usable name and id, a language the SDK has, and an empty folder to write in.
        /// </summary>
        public static ScaffoldResult Scaffold(ScaffoldRequest request, Templates templates)
        {
            var result = new ScaffoldResult();
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]*$"))
                result.Problems.Add($"'{request.Name}' isn't a name a project and a class can take: letters, digits and _, starting with a letter");
            if (!Languages.Contains(request.Language))
                result.Problems.Add($"'{request.Language}' isn't a language the SDK has: use {string.Join(", ", Languages)}");

            var id = string.IsNullOrWhiteSpace(request.Id) ? IdFrom(name ?? "") : request.Id.Trim();
            if (!InstallerLogic.IsValidAdapterId(id))
                result.Problems.Add($"'{id}' isn't an adapter id: lowercase letters, digits, '.', '_' and '-'");

            var directory = Path.GetFullPath(Path.Combine(request.ParentDirectory ?? ".", name ?? ""));
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                result.Problems.Add($"{directory} already exists and isn't empty");
            if (!result.Succeeded) return result;

            var files = templates(name, id, request.Language, request.Kind).ToList();
            Directory.CreateDirectory(directory);
            result.ProjectDirectory = directory;
            foreach (var (file, content) in files)
            {
                var path = Path.Combine(directory, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

        /// <summary>AcmeOrders → Acme Orders</summary>
        public static string Spaced(string name) => Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");

        /// <summary>The package.json a Node adapter starts with, naming the SDK and any <paramref name="dependencies"/>.</summary>
        public static string NodePackageJson(string id, bool typeScript, IDictionary<string, string> dependencies = null)
        {
            var package = new JsonObject { ["name"] = id, ["private"] = true };
            if (typeScript) package["type"] = "module";
            package["engines"] = new JsonObject { ["node"] = ">=22" };
            var deps = new JsonObject { ["@simplyworks/sw-serverless"] = NodeSdkVersion };
            foreach (var (name, version) in dependencies ?? new Dictionary<string, string>()) deps[name] = version;
            package["dependencies"] = deps;
            return package.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>The tsconfig.json a TypeScript adapter starts with: what Node can strip, checked by tsc --noEmit.</summary>
        public const string TsConfig = """
            {
              // For your editor and tsc --noEmit. The build doesn't compile: Node strips the types, so
              // only syntax that strips cleanly is allowed (no enums or namespaces).
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
            """;

        /// <summary>What a template's README says about the language, after "An adapter".</summary>
        public static string InLanguage(string language) => language switch
        {
            "python" => " in Python 3.12 or later",
            "typescript" => " in TypeScript, for Node 22 or later",
            "node" => " in JavaScript, for Node 22 or later",
            _ => " in .NET",
        };

        // ------------------------------------------------------------------ the generic adapter

        static IEnumerable<(string File, string Content)> Generic(string name, string id, string language, string kind)
        {
            var entry = language switch { "python" => "main.py", "node" => "main.js", "typescript" => "main.ts", _ => null };
            var runtime = language switch { "python" => "python", "node" or "typescript" => "node", _ => null };
            yield return ("adapter.json", runtime == null
                ? $$"""
                    {
                      "id": "{{id}}",
                      "version": "0.1.0",
                      "displayName": "{{Spaced(name)}}",
                      "summary": "What this adapter does, in one sentence.",
                      "lifecycle": "classic"
                    }
                    """
                : $$"""
                    {
                      "id": "{{id}}",
                      "version": "0.1.0",
                      "displayName": "{{Spaced(name)}}",
                      "summary": "What this adapter does, in one sentence.",
                      "runtime": "{{runtime}}",
                      "entry": "{{entry}}"
                    }
                    """);

            switch (language)
            {
                case "python":
                    yield return ("main.py", $$""""
                        import sw_serverless as sw


                        class {{name}}:
                            """Greets whoever it is asked to, the way its settings say."""

                            def __init__(self):
                                # Declare settings here; read them in the commands with sw.value_of.
                                sw.expect("Greeting", "Hello", description="What to say before the name.")
                                sw.expect("ApiKey", secret=True, required=False, description="A key, to show how a secret is declared.")

                            @sw.command("Greet", description="Greets someone by name.")
                            def greet(self, name: str) -> str:
                                return f"{sw.value_of('Greeting')}, {name}!"

                            @sw.command("Count", description="Counts the words in a text.")
                            def count(self, text: str) -> int:
                                return len(text.split())


                        if __name__ == "__main__":
                            sw.run({{name}})
                        """");
                    yield return ("requirements.txt", $$"""
                        # What the adapter imports, pinned, one per line; sw-serverless build vendors them into
                        # the package. The SDK is vendored by the build itself; it's listed for your editor.
                        sw-serverless=={{PythonSdkVersion}}
                        """);
                    yield return (".gitignore", "__pycache__/\n.venv/\nbin/\nsettings.json\n");
                    break;

                case "node":
                case "typescript":
                    var ts = language == "typescript";
                    yield return (entry, ts
                        ? $$"""
                            import { expect, run, valueOf } from "@simplyworks/sw-serverless";

                            /** Greets whoever it is asked to, the way its settings say. */
                            class {{name}} {
                              static commands = {
                                Greet: { method: "greet", input: "string", output: "string", description: "Greets someone by name." },
                                Count: { method: "count", input: "string", output: "json", description: "Counts the words in a text." },
                              };

                              constructor() {
                                // Declare settings here; read them in the commands with valueOf.
                                expect("Greeting", { default: "Hello", description: "What to say before the name." });
                                expect("ApiKey", { secret: true, required: false, description: "A key, to show how a secret is declared." });
                              }

                              greet(name: string): string {
                                return `${valueOf("Greeting")}, ${name}!`;
                              }

                              count(text: string): number {
                                return text.split(/\s+/).filter(Boolean).length;
                              }
                            }

                            run({{name}});
                            """
                        : $$"""
                            const { expect, run, valueOf } = require("@simplyworks/sw-serverless");

                            /** Greets whoever it is asked to, the way its settings say. */
                            class {{name}} {
                              static commands = {
                                Greet: { method: "greet", input: "string", output: "string", description: "Greets someone by name." },
                                Count: { method: "count", input: "string", output: "json", description: "Counts the words in a text." },
                              };

                              constructor() {
                                // Declare settings here; read them in the commands with valueOf.
                                expect("Greeting", { default: "Hello", description: "What to say before the name." });
                                expect("ApiKey", { secret: true, required: false, description: "A key, to show how a secret is declared." });
                              }

                              greet(name) {
                                return `${valueOf("Greeting")}, ${name}!`;
                              }

                              count(text) {
                                return text.split(/\s+/).filter(Boolean).length;
                              }
                            }

                            run({{name}});
                            """);
                    yield return ("package.json", NodePackageJson(id, ts));
                    if (ts) yield return ("tsconfig.json", TsConfig);
                    yield return (".gitignore", "node_modules/\nbin/\nsettings.json\n");
                    break;

                default:
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
                          </ItemGroup>

                        </Project>
                        """);
                    yield return ("Program.cs", $$"""
                        using SW.Serverless.Sdk;

                        namespace {{name}};

                        /// <summary>Greets whoever it is asked to, the way its settings say.</summary>
                        public class Adapter
                        {
                            public Adapter()
                            {
                                // Declare settings here; read them in the commands, never in the constructor.
                                Runner.Expect("Greeting", "Hello", description: "What to say before the name.");
                                Runner.Expect("ApiKey", optional: true, isPrivate: true, description: "A key, to show how a secret is declared.");
                            }

                            [AdapterCommand(Description = "Greets someone by name.")]
                            public Task<string> Greet(string name) =>
                                Task.FromResult($"{Runner.StartupValueOf("Greeting")}, {name}!");

                            [AdapterCommand(Description = "Counts the words in a text.")]
                            public Task<int> Count(string text) =>
                                Task.FromResult(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
                        }

                        static class Program
                        {
                            static Task Main() => Runner.Run(new Adapter());
                        }
                        """);
                    yield return (".gitignore", "bin/\nobj/\nsettings.json\n");
                    break;
            }

            yield return ("settings.example.json", """
                {
                  "Greeting": "Hello",
                  "ApiKey": "put a test key here, and keep this file out of version control once it holds one"
                }
                """);

            yield return ("README.md", $$"""
                # {{Spaced(name)}}

                An SW-Serverless adapter{{InLanguage(language)}}.

                ```sh
                sw-serverless build                                        # builds bin/serverless/{{id}}-0.1.0.zip
                cp settings.example.json settings.json                    # then fill in real values
                sw-serverless test --settings settings.json               # starts it as a host would and checks it
                sw-serverless run --settings settings.json --call Greet --input Ada
                sw-serverless publish bin/serverless/{{id}}-0.1.0.zip      # with your storage flags
                ```

                Settings are declared in code; `sw-serverless build` writes them into the package's
                manifest, so a host knows what to ask for without starting the adapter.
                """);
        }
    }
}
