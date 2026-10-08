using CommandLine;
using SW.Serverless.Installer.Shared;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.Installer
{
    public static class Program
    {
        public const int Success = 0;
        public const int Failure = 1;

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
            var result = parser.ParseArguments<CliOptions>(args);

            if (result is Parsed<CliOptions> parsed)
                return await RunOptions(parsed.Value, environment);

            // --help and --version are reported by the parser as "errors", but asking for them is
            // not a failure.
            var errors = ((NotParsed<CliOptions>)result).Errors;
            return errors.All(e => e.Tag is ErrorType.HelpRequestedError or ErrorType.VersionRequestedError
                    or ErrorType.HelpVerbRequestedError)
                ? Success
                : Failure;
        }

        private static async Task<ServerlessUploadOptions> GetServerlessUploadOptions(
            CliOptions options, Func<string, string> environment)
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

                var uploadOptions = await GetServerlessUploadOptions(opts, environment);
                var installer = new InstallerLogic();

                tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                var publishPath = Path.Combine(tempPath, "publish");

                if (!installer.BuildPublish(opts.ProjectPath, publishPath)) return Failure;

                var entryAssembly = InstallerLogic.ResolveEntryAssembly(publishPath, opts.ProjectPath);
                if (entryAssembly == null) return Failure;

                // Beside the published output rather than inside it, so it is never zipped into
                // itself and an adapter id cannot collide with a published file.
                var zipFileName = Path.Combine(tempPath, "adapter.zip");

                if (!installer.Compress(publishPath, zipFileName)) return Failure;

                // Read from the published assembly rather than asked for: the lifecycle is a fact
                // about the code, and a host that has to be told it separately will eventually be
                // told wrong — which is how a resident adapter ends up offered somewhere only a
                // classic one can run.
                var description = AdapterDescriber.Describe(publishPath, entryAssembly);

                // An explicit --kind wins, for an adapter whose author has not declared one.
                if (!string.IsNullOrWhiteSpace(opts.Kind)) description.Kind = opts.Kind.Trim();

                if (!await installer.PushToCloud(zipFileName, entryAssembly, uploadOptions, description))
                    return Failure;

                return Success;
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
    }
}
