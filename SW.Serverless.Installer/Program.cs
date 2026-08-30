using CommandLine;
using SW.Serverless.Installer.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SW.CloudFiles.OC;
using SW.PrimitiveTypes;

namespace SW.Serverless.Installer
{
    class Program
    {
        private static async Task Main(string[] args)
        {
            var parser = Parser.Default.ParseArguments<CliOptions>(args);
            await parser
                .WithParsedAsync(RunOptions)
                .Result
                .WithNotParsedAsync(HandleParseError);
        }

        private static Task HandleParseError(IEnumerable<Error> arg)
        {
            return Task.CompletedTask;
        }


        private static async Task<ServerlessUploadOptions> GetServerlessUploadOptions(CliOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.CloudFilesConfigPath))
                return new ServerlessUploadOptions
                {
                    AccessKeyId = options.AccessKeyId,
                    SecretAccessKey = options.SecretAccessKey,
                    ServiceUrl = options.ServiceUrl,
                    BucketName = options.BucketName,
                    Version = options.Version,
                    Provider = options.Provider,
                    AdapterId = options.AdapterId,
                };


            var rawFile = await File.ReadAllTextAsync(options.CloudFilesConfigPath);

            if (string.IsNullOrWhiteSpace(rawFile))
                throw new SWException($"Invalid cloud Files config path, {options.CloudFilesConfigPath}");


            var fileData = JsonConvert.DeserializeObject<FileData>(rawFile);
            var data = fileData.CloudFiles;
            return new ServerlessUploadOptions
            {
                Version = options.Version,
                AdapterId = options.AdapterId,
                Provider = options.Provider ?? data.Provider,
                AccessKeyId = data.AccessKeyId,
                SecretAccessKey = data.SecretAccessKey,
                ServiceUrl = data.ServiceUrl,
                BucketName = data.BucketName,
                Region = data.Region,
                FingerPrint = data.FingerPrint,
                TenantId = data.TenantId,
                UserId = data.UserId,
                RSAKey = data.RSAKey,
                NamespaceName = data.NamespaceName
            };
        }

        static async Task RunOptions(CliOptions opts)
        {
            var installer = new InstallerLogic();

            try
            {
                Environment.ExitCode = 1;

                var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

                if (!installer.BuildPublish(opts.ProjectPath, tempPath)) return;

                var entryAssembly = ResolveEntryAssembly(tempPath, opts.ProjectPath);

                if (entryAssembly == null) return;

                var zipFileName = Path.Combine(tempPath, $"{opts.AdapterId}");

                if (!installer.Compress(tempPath, zipFileName)) return;

                if (!await installer.PushToCloud(zipFileName, entryAssembly, await GetServerlessUploadOptions(opts)))
                    return;

                if (!installer.Cleanup(tempPath)) return;

                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        static string ResolveEntryAssembly(string publishPath, string projectPath)
        {
            // dotnet publish emits exactly one <AssemblyName>.runtimeconfig.json, for the startable
            // assembly. That is more reliable than assuming the assembly is named after the project
            // file, which is wrong whenever the project sets AssemblyName.
            const string runtimeConfigSuffix = ".runtimeconfig.json";
            var runtimeConfigs = Directory.GetFiles(publishPath, $"*{runtimeConfigSuffix}", SearchOption.TopDirectoryOnly);

            var entryAssembly = runtimeConfigs.Length == 1
                ? $"{Path.GetFileName(runtimeConfigs[0])[..^runtimeConfigSuffix.Length]}.dll"
                : $"{Path.GetFileNameWithoutExtension(projectPath)}.dll";

            if (File.Exists(Path.Combine(publishPath, entryAssembly)))
            {
                Console.WriteLine($"Entry assembly is {entryAssembly}.");
                return entryAssembly;
            }

            Console.WriteLine(
                $"Entry assembly '{entryAssembly}' is not in the published output. " +
                "The adapter would fail to start, so nothing was uploaded.");

            return null;
        }
    }
}