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
        public static readonly IReadOnlyList<string> Languages = new[] { "dotnet" };

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
            foreach (var (file, content) in DotnetFiles(name, id, request.Kind))
            {
                var path = Path.Combine(directory, file);
                File.WriteAllText(path, content.Replace("\r\n", "\n"));
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

        static string Spaced(string name) => Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
    }
}
