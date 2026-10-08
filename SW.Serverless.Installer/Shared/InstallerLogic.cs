using SW.PrimitiveTypes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SW.Serverless.Installer.Shared
{
    public class InstallerLogic
    {
        private static readonly Regex AdapterIdPattern = new(@"^[a-z0-9][a-z0-9._-]*$");

        /// <summary>
        /// An adapter id becomes a storage key (adapters/{id}), so it is held to characters that
        /// cannot leave that folder: lowercase letters, digits, '.', '_' and '-', starting with a
        /// letter or digit. No separators, and no '.' or '..' on their own.
        /// </summary>
        public static bool IsValidAdapterId(string adapterId) =>
            !string.IsNullOrEmpty(adapterId) && AdapterIdPattern.IsMatch(adapterId);

        public bool BuildPublish(string projectPath, string outputPath)
        {
            Console.WriteLine("Building and publishing...");

            var process = new Process
            {
                EnableRaisingEvents = true,
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    Arguments = $"publish \"{projectPath}\" -c Release -o \"{outputPath}\"",
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

        /// <summary>
        /// The assembly the runtime starts, as a file name inside <paramref name="publishPath"/>,
        /// or null when none of the candidates is actually there.
        ///
        /// Assuming it is named after the project file is wrong whenever the project sets
        /// AssemblyName, and the adapter then fails at its first invocation rather than here. So
        /// the candidates are, in order: what MSBuild says AssemblyName is, the literal value in
        /// the csproj, the single *.runtimeconfig.json publish emits for a startable assembly, and
        /// only then the project file name. The first one present in the output wins.
        /// </summary>
        public static string ResolveEntryAssembly(string publishPath, string projectPath)
        {
            var candidates = new List<string>
            {
                AssemblyNameFromMsBuild(projectPath),
                AssemblyNameFromProjectFile(projectPath),
                AssemblyNameFromRuntimeConfig(publishPath),
                Path.GetFileNameWithoutExtension(projectPath),
            };

            foreach (var name in candidates.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            {
                var entryAssembly = $"{name.Trim()}.dll";
                if (File.Exists(Path.Combine(publishPath, entryAssembly)))
                {
                    Console.WriteLine($"Entry assembly is {entryAssembly}.");
                    return entryAssembly;
                }
            }

            Console.WriteLine(
                $"No entry assembly for '{projectPath}' was found in the published output. " +
                "The adapter would fail to start, so nothing was uploaded.");

            return null;
        }

        private static string AssemblyNameFromMsBuild(string projectPath)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("dotnet")
                {
                    Arguments = $"msbuild \"{projectPath}\" -getProperty:AssemblyName -p:Configuration=Release",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (process == null) return null;

                var stderr = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                _ = stderr.Result;

                return process.ExitCode == 0 ? output.Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        public static string AssemblyNameFromProjectFile(string projectPath)
        {
            try
            {
                var value = XDocument.Load(projectPath).Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value.Trim();

                // A value built from properties ($(...)) cannot be evaluated here.
                return string.IsNullOrEmpty(value) || value.Contains("$(") ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private static string AssemblyNameFromRuntimeConfig(string publishPath)
        {
            const string suffix = ".runtimeconfig.json";
            try
            {
                var configs = Directory.GetFiles(publishPath, $"*{suffix}", SearchOption.TopDirectoryOnly);
                return configs.Length == 1 ? Path.GetFileName(configs[0])[..^suffix.Length] : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Zips everything under <paramref name="path"/>. Every file must make it in: a file that
        /// cannot be read fails the packaging, because the alternative is uploading an adapter
        /// that is missing a dependency and only finds out when it is invoked.
        /// </summary>
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

                using (var stream = new FileStream(zipFileName, FileMode.Create, FileAccess.Write))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (var file in filesToCompress)
                    {
                        if (skipExtensions.Contains(Path.GetExtension(file)))
                        {
                            Console.WriteLine($"Skipping {file} (excluded extension)");
                            continue;
                        }

                        var entryName = Path.GetRelativePath(path, file);

                        try
                        {
                            // Open file with shared read access
                            using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            var entry = archive.CreateEntry(entryName);

                            using var entryStream = entry.Open();
                            fileStream.CopyTo(entryStream);
                        }
                        catch (Exception ex)
                        {
                            throw new IOException($"Could not add {file} to the package: {ex.Message}", ex);
                        }
                    }
                }

                Console.WriteLine("Compressing files succeeded.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Compressing files failed: {ex.Message}");
                // A partial zip must not be left where a later step could pick it up.
                try { File.Delete(zipFileName); } catch { }
                return false;
            }
        }

        /// <summary>Lowercase hex SHA-256 of a file.</summary>
        public static string Sha256Of(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        /// <summary>
        /// What every uploaded adapter carries. Kind and Lifecycle are new, and both are written
        /// even when empty so a host can tell "this adapter declared nothing" from "this adapter
        /// predates the field" — the second still needs the old naming convention to classify it.
        /// Sha256 is the hex digest of the zip, for a runtime to check what it downloaded.
        /// </summary>
        private static Dictionary<string, string> BuildMetadata(
            string entryAssembly, AdapterDescription description, string sha256) => new()
        {
            { "EntryAssembly", entryAssembly },
            { "Lang", "dotnet" },
            { "Timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            { "Lifecycle", description?.Lifecycle ?? AdapterDescription.ClassicLifecycle },
            { "Kind", description?.Kind ?? "" },
            { "Sha256", sha256 },
        };

        /// <summary>
        /// The version names already published under <paramref name="dir"/>. Listed WITH the
        /// trailing slash, so publishing "foo" does not count the versions of "foo-bar", and
        /// limited to direct children of the folder.
        /// </summary>
        public static List<string> ExistingVersions(string dir, IEnumerable<string> keys)
        {
            var prefix = dir.TrimEnd('/') + "/";
            return keys
                .Where(k => k != null && k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k[prefix.Length..])
                .Where(name => name.Length > 0 && !name.Contains('/'))
                .ToList();
        }

        private static async Task UploadVersioned(ICloudFilesService cloudService, Stream zipFileStream,
            string adapterId,
            string entryAssembly, string version, AdapterDescription description, string sha256)
        {
            var dir = $"adapters/{adapterId}".ToLower();
            var list = await cloudService.ListAsync($"{dir}/");
            var fileName = Semver.GetNewVersion(version, ExistingVersions(dir, list.Select(i => i.Key)));
            var path = $"{dir}/{fileName}";
            Console.WriteLine($"Uploading to {path} Versioned");
            await cloudService.WriteAsync(zipFileStream, new WriteFileSettings
            {
                ContentType = "application/zip",
                Key = path,
                Metadata = BuildMetadata(entryAssembly, description, sha256)
            });
        }

        private static async Task UploadLegacy(ICloudFilesService cloudService, Stream zipFileStream, string adapterId,
            string entryAssembly, AdapterDescription description, string sha256)
        {
            var path = $"adapters/{adapterId}".ToLower();
            Console.WriteLine($"Uploading to {path}");
            await cloudService.WriteAsync(zipFileStream, new WriteFileSettings
            {
                ContentType = "application/zip",
                Key = path,
                Metadata = BuildMetadata(entryAssembly, description, sha256)
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
                if (!IsValidAdapterId(options.AdapterId?.ToLowerInvariant()))
                    throw new SWException(
                        $"Invalid adapter id '{options.AdapterId}'. Use lowercase letters, digits, '.', '_' and '-'.");

                Console.WriteLine("Starting...");
                var cloudService = CloudFilesFactory.Create(options);
                Console.WriteLine("Reading file...");

                var sha256 = Sha256Of(zipFilePath);
                await using var zipFileStream = File.OpenRead(zipFilePath);
                Console.WriteLine("Pushing to cloud...");

                description ??= new AdapterDescription();
                Console.WriteLine(
                    $"Lifecycle: {description.Lifecycle}"
                    + (string.IsNullOrEmpty(description.Kind) ? "" : $", kind: {description.Kind}")
                    + $", sha256: {sha256}");

                if (string.IsNullOrWhiteSpace(options.Version))
                {
                    await UploadLegacy(cloudService, zipFileStream, options.AdapterId, entryAssembly,
                        description, sha256);
                }
                else
                {
                    await UploadVersioned(cloudService, zipFileStream, options.AdapterId, entryAssembly,
                        options.Version, description, sha256);
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
                if (!Directory.Exists(tempPath)) return true;
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
