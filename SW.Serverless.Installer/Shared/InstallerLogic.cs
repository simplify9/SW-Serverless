using SW.PrimitiveTypes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Azure.Storage;
using Azure.Storage.Blobs;
using SW.CloudFiles.OC;
using CloudFilesService = SW.CloudFiles.AS.CloudFilesService;

namespace SW.Serverless.Installer.Shared
{
    public class InstallerLogic
    {
        public bool BuildPublish(string projectPath, string outputPath)
        {
            Console.WriteLine("Building and publishing...");

            var process = new Process
            {
                EnableRaisingEvents = true,
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    Arguments = $"publish \"{projectPath}\" -o \"{outputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };

            process.OutputDataReceived += OutputDataReceived;
            process.ErrorDataReceived += OutputDataReceived;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            var result = process.ExitCode == 0;

            Console.WriteLine($"Building and publishing {(result ? "succeeded" : "failed")}.");

            return result;
        }

        public bool Compress(string path, string zipFileName)
        {
            try
            {
                Console.WriteLine("Compressing files...");

                var skipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".http",
                    ".pdb",
                    ".xml"
                };

                var filesToCompress = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);

                using var stream = File.OpenWrite(zipFileName);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

                foreach (var file in filesToCompress)
                {
                    try
                    {
                        var extension = Path.GetExtension(file);
                        if (skipExtensions.Contains(extension))
                        {
                            Console.WriteLine($"Skipping {file} (excluded extension)");
                            continue;
                        }

                        var entryName = Path.GetRelativePath(path, file);

                        // Open file with shared read access
                        using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var entry = archive.CreateEntry(entryName);

                        using var entryStream = entry.Open();
                        fileStream.CopyTo(entryStream);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Skipping {file} (error: {ex.Message})");
                    }
                }

                Console.WriteLine("Compressing files succeeded.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Compressing files failed: {ex}");
                return false;
            }
        }




        /// <summary>
        /// What every uploaded adapter carries. Kind and Lifecycle are new, and both are written
        /// even when empty so a host can tell "this adapter declared nothing" from "this adapter
        /// predates the field" — the second still needs the old naming convention to classify it.
        /// </summary>
        private static Dictionary<string, string> BuildMetadata(
            string entryAssembly, AdapterDescription description) => new()
        {
            { "EntryAssembly", entryAssembly },
            { "Lang", "dotnet" },
            { "Timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            { "Lifecycle", description?.Lifecycle ?? AdapterDescription.ClassicLifecycle },
            { "Kind", description?.Kind ?? "" },
        };

        private static async Task UploadVersioned(ICloudFilesService cloudService, Stream zipFileStream,
            string adapterId,
            string entryAssembly, string version, AdapterDescription description)
        {
            var dir = $"adapters/{adapterId}".ToLower();
            var list = (await cloudService.ListAsync(dir)).ToList();
            var fileName = Semver.GetNewVersion(version, list.Select(i => i.Key.Split("/").Last()).ToList());
            var path = $"{dir}/{fileName}";
            Console.WriteLine($"Uploading to {path} Versioned");
            await cloudService.WriteAsync(zipFileStream, new WriteFileSettings
            {
                ContentType = "application/zip",
                Key = path,
                Metadata = BuildMetadata(entryAssembly, description)
            });
        }

        private static async Task UploadLegacy(ICloudFilesService cloudService, Stream zipFileStream, string adapterId,
            string entryAssembly, AdapterDescription description)
        {
            var path = $"adapters/{adapterId}".ToLower();
            Console.WriteLine($"Uploading to {path}");
            await cloudService.WriteAsync(zipFileStream, new WriteFileSettings
            {
                ContentType = "application/zip",
                Key = path,
                Metadata = BuildMetadata(entryAssembly, description)
            });
        }

        public async Task<bool> PushToCloud(
            string zipFilePath,
            string entryAssembly,
            ServerlessUploadOptions options,
            AdapterDescription description = null)
        {
            try
            {
                Console.WriteLine("Starting...");
                var cloudService = CloudFilesFactory.Create(options);
                Console.WriteLine("Reading file...");

                await using var zipFileStream = File.OpenRead(zipFilePath);
                Console.WriteLine("Pushing to cloud...");

                description ??= new AdapterDescription();
                Console.WriteLine(
                    $"Lifecycle: {description.Lifecycle}"
                    + (string.IsNullOrEmpty(description.Kind) ? "" : $", kind: {description.Kind}"));

                if (string.IsNullOrWhiteSpace(options.Version))
                {
                    await UploadLegacy(cloudService, zipFileStream, options.AdapterId, entryAssembly,
                        description);
                }
                else
                {
                    await UploadVersioned(cloudService, zipFileStream, options.AdapterId, entryAssembly,
                        options.Version, description);
                }

                Console.WriteLine("Pushing to cloud succeeded.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pushing to cloud failed: {ex}");
                return false;
            }
        }

        public bool Cleanup(string tempPath)
        {
            try
            {
                Console.WriteLine("Cleaning up...");
                Directory.Delete(tempPath, true);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleaning up failed: {ex}");
                return false;
            }
        }

        private static void OutputDataReceived(object sender, DataReceivedEventArgs args)
        {
            Console.WriteLine(args.Data);
        }
    }
}