using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SW.Serverless.Contract.Catalog;
using SW.Serverless.Runtimes;

namespace SW.Serverless.Tooling.Conformance
{
    /// <summary>
    /// Runs an adapter package the way a host does — installed from storage, started on its runtime,
    /// called by name — and checks it against its manifest, its own description and every contract
    /// it declares. What <c>sw-serverless test</c> runs, for an adapter in any language.
    /// </summary>
    public class ConformanceRunner
    {
        const string UnknownCommand = "__ConformanceNoSuchCommand__";

        public async Task<ConformanceReport> RunAsync(ConformanceOptions options)
        {
            var report = new ConformanceReport();
            var work = options.WorkDirectory ?? Path.Combine(Path.GetTempPath(), "swsl-conformance", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                await RunCoreAsync(options, report, work);
            }
            finally
            {
                if (options.WorkDirectory == null)
                    try { Directory.Delete(work, true); } catch { /* best effort */ }
            }
            return report;
        }

        async Task RunCoreAsync(ConformanceOptions options, ConformanceReport report, string work)
        {
            // ------------------------------------------------------------ the manifest
            var manifestPath = Path.Combine(options.PackageDirectory, AdapterManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                report.Fail("manifest", $"there is no {AdapterManifest.FileName} in {options.PackageDirectory}; sw-serverless build writes it");
                return;
            }

            AdapterManifest manifest;
            try
            {
                manifest = AdapterManifest.Parse(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                report.Fail("manifest", $"{AdapterManifest.FileName} can't be read: {ex.Message}");
                return;
            }

            var problems = manifest.Validate();
            if (problems.Count > 0) report.Fail("manifest", string.Join("; ", problems));
            else report.Pass("manifest");

            var entry = manifest.EntryFor(AdapterRuntimes.CurrentPlatform);
            if (string.IsNullOrWhiteSpace(entry) || !File.Exists(Path.Combine(options.PackageDirectory, entry)))
            {
                report.Fail("entry", $"the manifest's entry '{entry}' is not in the package");
                return;
            }

            // ------------------------------------------------------------ its own description
            options.Log("Asking the adapter to describe itself...");
            var description = await DescribeAsync(options, manifest, Path.Combine(options.PackageDirectory, entry), report);
            if (description != null) CheckSettings(manifest, description, report);

            // ------------------------------------------------------------ running it
            options.Log("Starting it the way a host does...");
            LocalAdapterHost session;
            try
            {
                session = await LocalAdapterHost.StartAsync(options.PackageDirectory, options.Settings, options.Runtimes,
                    options.CommandTimeoutSeconds, work, options.Limits);
                report.Pass("starts", manifest.IsResident ? "resident, attached over gRPC" : "classic session");
            }
            catch (Exception ex)
            {
                report.Fail("starts", ex.GetBaseException().Message);
                return;
            }

            await using (session)
            {
                foreach (var (contract, kinds) in ContractsToCheck(manifest, description, options, report))
                foreach (var kind in kinds)
                    await CheckKindAsync(session, contract, kind, description, options, report);

                await CheckUnknownCommandAsync(session, report);
            }
        }

        // ---------------------------------------------------------------- describe

        static async Task<AdapterSelfDescription> DescribeAsync(ConformanceOptions options, AdapterManifest manifest,
            string entryPath, ConformanceReport report)
        {
            var (description, problem) = await LocalAdapterHost.DescribeAsync(entryPath, manifest.Runtime, options.Runtimes);
            if (description == null)
                report.Fail("describe", problem);
            else if (string.IsNullOrWhiteSpace(description.SdkLanguage))
                report.Fail("describe", "the description doesn't name its SDK's language");
            else
                report.Pass("describe", $"{description.SdkLanguage} SDK {description.SdkVersion}, {description.Commands.Count} commands" +
                                        (description.Warnings.Count > 0 ? $"; warnings: {string.Join("; ", description.Warnings)}" : ""));
            return description;
        }

        /// <summary>The manifest's properties must be the settings the adapter itself declares.</summary>
        static void CheckSettings(AdapterManifest manifest, AdapterSelfDescription description, ConformanceReport report)
        {
            var declared = description.Settings.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            var listed = (manifest.Properties ?? new List<AdapterProperty>()).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var differences = new List<string>();

            foreach (var name in declared.Keys.Except(listed.Keys, StringComparer.OrdinalIgnoreCase))
                differences.Add($"'{name}' is declared by the adapter but missing from the manifest");
            foreach (var name in listed.Keys.Except(declared.Keys, StringComparer.OrdinalIgnoreCase))
                differences.Add($"'{name}' is in the manifest but the adapter doesn't declare it");
            foreach (var name in declared.Keys.Intersect(listed.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var (code, file) = (declared[name], listed[name]);
                if (code.Required != file.Required) differences.Add($"'{name}' is {(code.Required ? "" : "not ")}required in the adapter but {(file.Required ? "" : "not ")}in the manifest");
                if (code.Secret != file.Secret) differences.Add($"'{name}' is {(code.Secret ? "" : "not ")}secret in the adapter but {(file.Secret ? "" : "not ")}in the manifest");
                if (!code.Secret && (code.Default ?? "") != (file.Default ?? "")) differences.Add($"'{name}' defaults to '{code.Default}' in the adapter but '{file.Default}' in the manifest");
            }

            if (differences.Count > 0) report.Fail("settings match the manifest", string.Join("; ", differences) + " — sw-serverless build rewrites the manifest from the adapter");
            else report.Pass("settings match the manifest", $"{declared.Count} settings");
        }

        // ---------------------------------------------------------------- contracts

        static IEnumerable<(ContractDocument Contract, List<string> Kinds)> ContractsToCheck(AdapterManifest manifest,
            AdapterSelfDescription description, ConformanceOptions options, ConformanceReport report)
        {
            var declared = new Dictionary<string, int>(manifest.Contracts ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, version) in description?.Contracts ?? new Dictionary<string, int>())
                declared.TryAdd(name, version);

            if (declared.Count == 0)
                report.Skip("contracts", "it declares no contract, so only what every adapter must do is checked");

            var kinds = (manifest.Kinds ?? new List<string>()).Union(description?.Kinds ?? new List<string>(), StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var (name, version) in declared)
            {
                var contract = options.Contracts.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) && c.Version == version)
                               ?? ContractDocument.Known(name, version);
                if (contract == null)
                {
                    report.Fail($"contract {name} v{version}", "the kit doesn't know this contract; pass it with --contract");
                    continue;
                }

                var checkedKinds = kinds.Where(k => contract.Kinds.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
                if (checkedKinds.Count == 0)
                    report.Fail($"contract {name} v{version}", $"it implements none of the contract's kinds ({string.Join(", ", contract.Kinds)})");
                yield return (contract, checkedKinds);
            }
        }

        static async Task CheckKindAsync(LocalAdapterHost session, ContractDocument contract, string kind,
            AdapterSelfDescription description, ConformanceOptions options, ConformanceReport report)
        {
            var prefix = $"{contract.Name} {kind}";
            var methods = contract.MethodsOf(kind);

            if (description != null)
            {
                var commands = description.Commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
                var missing = methods.Where(m => !commands.Contains(m.Name)).Select(m => m.Name).ToList();
                if (missing.Count > 0) report.Fail($"{prefix}: methods", $"missing {string.Join(", ", missing)} (names are case-sensitive)");
                else report.Pass($"{prefix}: methods", string.Join(", ", methods.Select(m => m.Name)));
            }

            if (contract.IsSession(kind))
            {
                await CheckSessionAsync(session, contract, prefix, methods, options, report);
                return;
            }

            foreach (var method in methods)
            {
                if (method.Examples.Count == 0)
                {
                    report.Skip($"{prefix}: {method.Name}", "the contract gives no example to call it with");
                    continue;
                }

                for (var i = 0; i < method.Examples.Count; i++)
                {
                    var name = $"{prefix}: {method.Name} answers example {i + 1}";
                    try
                    {
                        var input = contract.IsText(method.Input) ? (object)(string)method.Examples[i] : method.Examples[i];
                        if (method.Output == null)
                        {
                            await session.CallVoidAsync(method.Name, input);
                            report.Pass(name);
                        }
                        else
                            await CheckOutputAsync(contract, method.Output, await session.CallAsync(method.Name, input), name, report);
                    }
                    catch (Exception ex)
                    {
                        report.Fail(name, $"the call failed: {ex.GetBaseException().Message}");
                    }
                }
            }
        }

        /// <summary>
        /// A session kind, such as a receiver, called once through in the contract's order: methods
        /// without input as they come, a text input fed the first id the session listed, and a
        /// destructive method only when allowed.
        /// </summary>
        static async Task CheckSessionAsync(LocalAdapterHost session, ContractDocument contract, string prefix,
            IReadOnlyList<ContractMethod> methods, ConformanceOptions options, ConformanceReport report)
        {
            string firstId = null;
            foreach (var method in methods)
            {
                var name = $"{prefix}: {method.Name}";
                if (method.Destructive && !options.AllowDelete)
                {
                    report.Skip(name, "it changes the source the settings point at; allow it with --allow-delete");
                    continue;
                }

                object input = null;
                if (method.Input != null)
                {
                    if (firstId == null)
                    {
                        report.Skip(name, "nothing was listed to call it with");
                        continue;
                    }
                    input = firstId;
                }

                try
                {
                    if (method.Output == null)
                    {
                        await session.CallVoidAsync(method.Name, input);
                        report.Pass(name);
                        continue;
                    }

                    var output = await session.CallAsync(method.Name, input);
                    await CheckOutputAsync(contract, method.Output, output, name, report);
                    if (firstId == null && TryParse(output) is JArray ids && ids.Count > 0 && ids[0].Type == JTokenType.String)
                        firstId = (string)ids[0];
                }
                catch (Exception ex)
                {
                    report.Fail(name, $"the call failed: {ex.GetBaseException().Message}");
                }
            }
        }

        static async Task CheckOutputAsync(ContractDocument contract, string type, string output, string name, ConformanceReport report)
        {
            var schema = await contract.SchemaOfAsync(type);
            if (schema == null)
            {
                report.Pass(name);
                return;
            }

            if (TryParse(output) is not { } json)
            {
                report.Fail(name, $"the answer isn't JSON: {Shorten(output)}");
                return;
            }

            var errors = schema.Validate(json);
            if (errors.Count > 0) report.Fail(name, $"the answer isn't a valid {type}: {string.Join("; ", errors.Select(e => e.ToString()))}");
            else report.Pass(name, $"a valid {type}");
        }

        /// <summary>An unknown command is refused with an error, and the adapter keeps answering.</summary>
        static async Task CheckUnknownCommandAsync(LocalAdapterHost session, ConformanceReport report)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    await session.CallAsync(UnknownCommand, null);
                    report.Fail("an unknown command is refused", "it answered a command it doesn't have");
                    return;
                }
                catch (TimeoutException)
                {
                    report.Fail("an unknown command is refused", "it didn't answer a command it doesn't have");
                    return;
                }
                catch (SW.Serverless.Resident.AdapterStoppedException ex)
                {
                    // Not a refusal: it was stopped — at a memory or CPU limit, say — and answers nothing now.
                    report.Fail("an unknown command is refused", ex.Message);
                    return;
                }
                catch (Exception) when (attempt == 1)
                {
                    // Refused; the second attempt shows it is still answering after that.
                }
                catch (Exception)
                {
                    report.Pass("an unknown command is refused", "with an error, and it kept answering");
                }
            }
        }

        static JToken TryParse(string text)
        {
            try { return string.IsNullOrWhiteSpace(text) ? null : JToken.Parse(text); }
            catch { return null; }
        }

        static string Shorten(string text) => text == null ? "nothing" : text.Length <= 200 ? text : text[..200] + "…";
    }
}
