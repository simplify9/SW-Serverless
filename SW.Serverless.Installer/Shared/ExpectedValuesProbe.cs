using Newtonsoft.Json.Linq;
using SW.Serverless.Contract.Catalog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SW.Serverless.Installer.Shared
{
    /// <summary>
    /// Asks a built CLASSIC adapter which startup values it expects, the way every host already
    /// does: start it, send the <c>{{expected}}</c> command, read the one-line answer.
    ///
    /// This is the only place the installer runs the code it packages — the describer only reads
    /// metadata. It is worth it because the answer is what the adapter itself will say to a host at
    /// runtime, so the published properties cannot drift from the code; and it is optional
    /// (<c>--no-probe</c>), because a build machine may not be somewhere an author's constructor
    /// should run.
    /// </summary>
    public static class ExpectedValuesProbe
    {
        // The classic wire format, copied rather than referenced: the installer must not depend on
        // the SDK it is packaging, and these strings are frozen by every adapter already published.
        const string Delimiter = "#!#";
        const string NullIdentifier = "{{null}}";
        const string NewLineIdentifier = "{{newline}}";
        const string ErrorIdentifier = "{{error}}";
        const string QuitCommand = "{{quit}}";
        const string ExpectedCommand = "{{expected}}";

        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// The adapter's expected values as manifest properties, in the order it declared them.
        /// Throws when the adapter could not be started or did not answer; the caller decides that
        /// this is a warning, not a failed publish.
        /// </summary>
        public static async Task<List<AdapterProperty>> ProbeAsync(
            string publishDirectory, string entryAssembly, TimeSpan? timeout = null)
        {
            var entryPath = Path.Combine(publishDirectory, entryAssembly);
            if (!File.Exists(entryPath))
                throw new FileNotFoundException($"{entryAssembly} is not in the published output.", entryPath);

            // Same arguments a host passes: serverless options, startup values, adapter values —
            // each base64 JSON. Empty values: an adapter declares what it expects without being
            // given any, and one that reads a value in its constructor fails here as it would on a
            // host that had not configured it.
            static string Empty() => Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet")
                {
                    Arguments = $"\"{entryPath}\" {Empty()} {Empty()} {Empty()}",
                    WorkingDirectory = publishDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                }
            };

            var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new StringBuilder();

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    answer.TrySetException(new InvalidOperationException(
                        "The adapter exited without answering." + Tail(errors)));
                    return;
                }

                if (args.Data.StartsWith(ErrorIdentifier, StringComparison.Ordinal))
                {
                    answer.TrySetException(new InvalidOperationException(
                        args.Data[ErrorIdentifier.Length..].Replace(NewLineIdentifier, "\n")));
                    return;
                }

                // Anything else an adapter prints to stdout is not the answer; a host would choke on
                // it, but a probe can afford to wait for the line that is.
                var segments = args.Data.Split(Delimiter);
                if (segments.Length == 3) answer.TrySetResult(segments[1].Replace(NewLineIdentifier, "\n"));
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data != null) lock (errors) errors.AppendLine(args.Data);
            };

            if (!process.Start()) throw new InvalidOperationException("The adapter could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.StandardInput.WriteLineAsync($"{Delimiter}{ExpectedCommand}{Delimiter}{NullIdentifier}{Delimiter}");
                await process.StandardInput.FlushAsync();

                var limit = timeout ?? DefaultTimeout;
                var finished = await Task.WhenAny(answer.Task, Task.Delay(limit));
                if (finished != answer.Task)
                    throw new TimeoutException($"The adapter did not answer within {limit.TotalSeconds:0} seconds.");

                return ToProperties(await answer.Task);
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        await process.StandardInput.WriteLineAsync(QuitCommand);
                        await process.StandardInput.FlushAsync();
                        if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                }
            }
        }

        /// <summary>
        /// The SDK's answer — a JSON object of name to StartupValue — as properties. Read as JSON
        /// rather than into StartupValue, so the declared order survives and an older or newer SDK's
        /// extra fields cannot break it.
        /// </summary>
        public static List<AdapterProperty> ToProperties(string json)
        {
            var properties = new List<AdapterProperty>();
            if (string.IsNullOrWhiteSpace(json) || json == NullIdentifier) return properties;

            foreach (var item in JObject.Parse(json).Properties())
            {
                var value = item.Value as JObject ?? new JObject();

                string Text(string name) =>
                    value.GetValue(name, StringComparison.OrdinalIgnoreCase) is JValue { Type: not JTokenType.Null } v
                        ? v.ToString()
                        : null;
                bool Flag(string name) =>
                    value.GetValue(name, StringComparison.OrdinalIgnoreCase) is JValue { Type: JTokenType.Boolean } v &&
                    (bool)v;

                var type = Text("Type");
                properties.Add(new AdapterProperty
                {
                    Name = item.Name,
                    Required = !Flag("Optional"),
                    Default = Text("Default"),
                    // The SDK only ever says "text"; anything a form would not understand is text too.
                    Type = type != null && AdapterProperty.KnownTypes.Contains(type) ? type : AdapterProperty.TextType,
                    Secret = Flag("Private"),
                    Description = Text("Description"),
                });
            }

            return properties;
        }

        static string Tail(StringBuilder errors)
        {
            string text;
            lock (errors) text = errors.ToString().Trim();
            if (text.Length == 0) return "";
            return " " + (text.Length > 2000 ? text[^2000..] : text);
        }
    }
}
