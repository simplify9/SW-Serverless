using CommandLine;
using SW.PrimitiveTypes;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Tooling;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.Installer
{
    public static class Program
    {
        public const int Success = 0;
        public const int Failure = 1;

        /// <summary>The commands besides publishing. Matched on the first argument only.</summary>
        static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
        {
            "promote", "versions", "withdraw",
            // For adapters in any language, over SW.Serverless.Tooling.
            "init", "build", "test", "run", "manifest", "publish",
        };

        private static Task<int> Main(string[] args) => RunAsync(args);

        /// <summary>
        /// The whole command, returning the process exit code. Anything other than a completed
        /// upload, including a bad command line, is non-zero, so a CI step that publishes an
        /// adapter fails when the publish did.
        /// </summary>
        public static async Task<int> RunAsync(string[] args, Func<string, string> environment = null)
        {
            // AutoVersion off: the parser's built-in --version would otherwise shadow ours, so
            // "--version minor" printed the tool version and exited 0 without publishing anything.
            using var parser = new Parser(s =>
            {
                s.HelpWriter = Console.Error;
                s.AutoVersion = false;
            });

            // A command is recognised before the publish parser sees anything, so every existing
            // "serverless <project> <id>" line parses exactly as before. A project file that happens
            // to be called "promote" is still a project.
            if (args.Length > 0 && Commands.Contains(args[0]) && !File.Exists(args[0]))
            {
                var rest = args[1..];
                return args[0] switch
                {
                    "promote" => await Run(parser.ParseArguments<PromoteCliOptions>(rest),
                        o => Promote(o, environment)),
                    "versions" => await Run(parser.ParseArguments<VersionsCliOptions>(rest),
                        o => Versions(o, environment)),
                    "init" => await Run(parser.ParseArguments<InitCliOptions>(rest), AdapterCommands.Init),
                    "build" => await Run(parser.ParseArguments<BuildCliOptions>(rest), AdapterCommands.Build),
                    "test" => await Run(parser.ParseArguments<TestCliOptions>(rest), AdapterCommands.Test),
                    "run" => await Run(parser.ParseArguments<RunCliOptions>(rest), AdapterCommands.Run),
                    "manifest" => await Run(parser.ParseArguments<ManifestCliOptions>(rest), AdapterCommands.Manifest),
                    "publish" => await Run(parser.ParseArguments<PublishPackageCliOptions>(rest),
                        o => AdapterCommands.Publish(o, environment)),
                    _ => await Run(parser.ParseArguments<WithdrawCliOptions>(rest),
                        o => Withdraw(o, environment)),
                };
            }

            return await Run(parser.ParseArguments<CliOptions>(args), o => RunOptions(o, environment));
        }

        static async Task<int> Run<T>(ParserResult<T> result, Func<T, Task<int>> run)
        {
            if (result is Parsed<T> parsed)
                return await run(parsed.Value);

            // --help and --version are reported by the parser as "errors", but asking for them is
            // not a failure.
            var errors = ((NotParsed<T>)result).Errors;
            return errors.All(e => e.Tag is ErrorType.HelpRequestedError or ErrorType.VersionRequestedError
                    or ErrorType.HelpVerbRequestedError)
                ? Success
                : Failure;
        }

        private static async Task<ServerlessUploadOptions> GetServerlessUploadOptions(
            StorageCliOptions options, Func<string, string> environment)
        {
            var configJson = string.IsNullOrWhiteSpace(options.CloudFilesConfigPath)
                ? null
                : await File.ReadAllTextAsync(options.CloudFilesConfigPath);

            return UploadOptionsResolver.Resolve(options, configJson, environment);
        }

        static async Task<int> RunOptions(CliOptions opts, Func<string, string> environment)
        {
            string tempPath = null;

            try
            {
                // Everything that can be checked without building is checked first, so a typo
                // fails in a second rather than after a publish.
                if (!InstallerLogic.IsValidAdapterId(opts.AdapterId?.ToLowerInvariant()))
                {
                    Console.WriteLine(
                        $"Invalid adapter id '{opts.AdapterId}'. Use lowercase letters, digits, '.', '_' and '-', " +
                        "starting with a letter or digit.");
                    return Failure;
                }

                if (!File.Exists(opts.ProjectPath))
                {
                    Console.WriteLine($"Project file '{opts.ProjectPath}' does not exist.");
                    return Failure;
                }

                if (opts.NoPromote && string.IsNullOrWhiteSpace(opts.Version))
                {
                    Console.WriteLine("--no-promote needs -v: an unversioned upload always replaces what runs.");
                    return Failure;
                }

                var uploadOptions = await GetServerlessUploadOptions(opts, environment);
                var installer = new InstallerLogic();

                tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                var publishPath = Path.Combine(tempPath, "publish");

                if (!installer.BuildPublish(opts.ProjectPath, publishPath)) return Failure;

                var entryAssembly = InstallerLogic.ResolveEntryAssembly(publishPath, opts.ProjectPath);
                if (entryAssembly == null) return Failure;

                Console.WriteLine("Starting...");
                var files = CloudFilesFactory.Create(uploadOptions);

                await PackagePublisher.PublishAsync(files, new PublishRequest
                {
                    AdapterId = opts.AdapterId,
                    ProjectPath = opts.ProjectPath,
                    PublishPath = publishPath,
                    EntryAssembly = entryAssembly,
                    // Beside the published output rather than inside it, so it is never zipped into
                    // itself and an adapter id cannot collide with a published file.
                    WorkPath = tempPath,
                    Version = opts.Version,
                    Promote = !opts.NoPromote,
                    Probe = !opts.NoProbe,
                    // An explicit --kind wins, for an adapter whose author has not declared one.
                    Kind = opts.Kind?.Trim(),
                    ReleaseNotes = opts.Notes,
                    PublishedBy = UploadOptionsResolver.ResolvePublishedBy(opts.PublishedBy, environment),
                });

                Console.WriteLine("Pushing to cloud succeeded.");
                return Success;
            }
            catch (SWException ex)
            {
                // A problem the user can fix: the message says what, a stack trace would bury it.
                Console.WriteLine(ex.Message);
                return Failure;
            }
            catch (ArgumentException ex)
            {
                // Semver's verdicts: an existing or lower version, or an unknown bump.
                Console.WriteLine(ex.Message);
                return Failure;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return Failure;
            }
            finally
            {
                // A failed build or upload must not leave a copy of the adapter in the temp folder.
                if (tempPath != null) new InstallerLogic().Cleanup(tempPath);
            }
        }

        // ---------------------------------------------------------------- commands

        static Task<int> Promote(PromoteCliOptions opts, Func<string, string> environment) =>
            WithRepository(opts, opts.AdapterId, environment, async (repository, id) =>
            {
                var work = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                try
                {
                    await repository.PromoteAsync(id, opts.Version?.Trim(), work);
                }
                finally
                {
                    new InstallerLogic().Cleanup(work);
                }
            });

        static Task<int> Withdraw(WithdrawCliOptions opts, Func<string, string> environment) =>
            WithRepository(opts, opts.AdapterId, environment,
                (repository, id) => repository.WithdrawAsync(id, opts.Version?.Trim()));

        static Task<int> Versions(VersionsCliOptions opts, Func<string, string> environment) =>
            WithRepository(opts, opts.AdapterId, environment, async (repository, id) =>
            {
                var listing = await repository.ListVersionsAsync(id);
                foreach (var line in FormatVersions(id, listing)) Console.WriteLine(line);
            });

        static async Task<int> WithRepository(StorageCliOptions opts, string adapterId,
            Func<string, string> environment, Func<AdapterRepository, string, Task> action)
        {
            try
            {
                var id = adapterId?.ToLowerInvariant();
                if (!InstallerLogic.IsValidAdapterId(id))
                {
                    Console.WriteLine($"Invalid adapter id '{adapterId}'.");
                    return Failure;
                }

                var files = CloudFilesFactory.Create(await GetServerlessUploadOptions(opts, environment));
                await action(new AdapterRepository(files), id);
                return Success;
            }
            catch (SWException ex)
            {
                Console.WriteLine(ex.Message);
                return Failure;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return Failure;
            }
        }

        /// <summary>Runs <paramref name="action"/> against the storage the options name, reporting a failure as a message.</summary>
        internal static async Task<int> WithStorage(StorageCliOptions opts, Func<string, string> environment,
            Func<SW.PrimitiveTypes.ICloudFilesService, Task> action)
        {
            try
            {
                await action(CloudFilesFactory.Create(await GetServerlessUploadOptions(opts, environment)));
                return Success;
            }
            catch (SWException ex)
            {
                Console.WriteLine(ex.Message);
                return Failure;
            }
            catch (ArgumentException ex)
            {
                // Semver's verdicts: an existing or lower version, or an unknown bump.
                Console.WriteLine(ex.Message);
                return Failure;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return Failure;
            }
        }

        public static IEnumerable<string> FormatVersions(string adapterId, VersionListing listing)
        {
            if (listing.Versions.Count == 0)
            {
                yield return $"'{adapterId}' has no published versions.";
                yield break;
            }

            yield return $"{adapterId}: current {listing.Current ?? "(unversioned package)"}" +
                         (listing.FromCatalog ? "" : "  — no catalog entry; read from the packages");
            yield return $"  {"VERSION",-18} {"PUBLISHED (UTC)",-17} {"BY",-20} {"SHA256",-12}";
            foreach (var row in Enumerable.Reverse(listing.Versions))
            {
                yield return $"{(row.Current ? "*" : " ")} {row.Version,-18} " +
                             $"{row.PublishedOn?.UtcDateTime.ToString("yyyy-MM-dd HH:mm") ?? "",-17} " +
                             $"{row.PublishedBy ?? "",-20} {AdapterRepository.Short(row.Sha256),-12}" +
                             (row.Withdrawn ? " withdrawn" : "");
            }
        }
    }
}
