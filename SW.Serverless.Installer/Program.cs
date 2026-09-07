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
                    Kind = options.Kind,
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

                var zipFileName = Path.Combine(tempPath, $"{opts.AdapterId}");

                if (!installer.Compress(tempPath, zipFileName)) return;

                var projectFileName = Path.GetFileName(opts.ProjectPath);
                var entryAssembly = $"{projectFileName!.Remove(projectFileName.LastIndexOf('.'))}.dll";

                // Read from the published assembly rather than asked for: the lifecycle is a fact
                // about the code, and a host that has to be told it separately will eventually be
                // told wrong — which is how a resident adapter ends up offered somewhere only a
                // classic one can run.
                var description = AdapterDescriber.Describe(tempPath, entryAssembly);

                // An explicit --kind wins, for an adapter whose author has not declared one.
                if (!string.IsNullOrWhiteSpace(opts.Kind)) description.Kind = opts.Kind.Trim();

                var uploadOptions = await GetServerlessUploadOptions(opts);

                if (!await installer.PushToCloud(zipFileName, entryAssembly, uploadOptions, description))
                    return;

                if (!installer.Cleanup(tempPath)) return;

                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }
    }
}