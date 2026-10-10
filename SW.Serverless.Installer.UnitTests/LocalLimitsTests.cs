using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SW.Serverless.Runtimes;
using SW.Serverless.Tooling;
using SW.Serverless.Tooling.Building;
using SW.Serverless.Tooling.Conformance;

namespace SW.Serverless.Installer.UnitTests;

/// <summary>
/// Memory and CPU ceilings on an adapter run by LocalAdapterHost, as a tool trying drafts on a
/// shared server sets them: an adapter over one is stopped and the call says which limit, and an
/// adapter run without limits runs as it always has.
/// </summary>
[TestClass]
public class LocalLimitsTests
{
    const long Limit = 150L * 1024 * 1024;

    static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SW.Serverless.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    static string WorkFolder()
    {
        var folder = Path.Combine(RepositoryRoot(), ".test-work", "limits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(Path.Combine(RepositoryRoot(), ".test-work"), true); } catch { }
    }

    // Allocates in steps, so a watchdog sampling every second sees it climb; touched (b"x" * n), so
    // it is resident and not merely reserved. Capped, so a limit that failed costs a second, not the machine.
    const string PythonHog = """
        import os
        import time

        import sw_serverless as sw


        class Hog:
            def __init__(self):
                sw.expect("HogOnStart", "0", required=False)

            def start(self):
                if sw.value_of("HogOnStart") != "0":
                    self.allocate(1000)

            @sw.command("Allocate")
            def allocate(self, megabytes: int) -> int:
                held = []
                for _ in range(megabytes // 10):
                    held.append(b"x" * (10 << 20))
                    time.sleep(0.05)
                return len(held) * 10

            @sw.command("Spin")
            def spin(self, seconds: int) -> int:
                end, turns = time.monotonic() + seconds, 0
                while time.monotonic() < end:
                    turns += 1
                return turns

            @sw.command("Limit")
            def limit(self) -> str:
                return os.environ.get("SW_SERVERLESS_MEMORY_LIMIT_BYTES", "none")


        if __name__ == "__main__":
            sw.run(Hog)
        """;

    // V8 heap, not Buffers: the old space is what --max-old-space-size holds.
    const string NodeHog = """
        const { run } = require("@simplyworks/sw-serverless");

        class Hog {
          static commands = { Allocate: { method: "allocate", input: "json", output: "json" } };

          async allocate(megabytes) {
            const held = [];
            for (let i = 0; i < megabytes; i++) {
              held.push(new Array(128 * 1024).fill(i + 0.5));
              if (i % 10 === 0) await new Promise((r) => setTimeout(r, 50));
            }
            return held.length;
          }
        }

        run(Hog);
        """;

    static async Task<string> BuildAsync(string runtime, string entry, string code)
    {
        var project = Path.Combine(WorkFolder(), "hog");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, entry), code);
        File.WriteAllText(Path.Combine(project, "adapter.json"),
            $$"""{ "id": "test.hog.{{runtime}}", "version": "0.1.0", "runtime": "{{runtime}}", "entry": "{{entry}}" }""");
        var result = await PackageBuilder.BuildAsync(new BuildRequest { ProjectDirectory = project, IncludeSource = false });
        Assert.IsTrue(result.Succeeded, string.Join("; ", result.Problems));
        return result.PackageDirectory;
    }

    static readonly Lazy<Task<string>> PythonPackage = new(() => BuildAsync("python", "main.py", PythonHog));

    static Task<LocalAdapterHost> Start(string package, LocalAdapterLimits? limits, IDictionary<string, string>? settings = null) =>
        LocalAdapterHost.StartAsync(package, settings ?? new Dictionary<string, string>(), commandTimeoutSeconds: 30, limits: limits);

    static async Task<Exception> FailureOf(Func<Task> call)
    {
        try { await call(); }
        catch (Exception ex) { return ex; }
        Assert.Fail("the call succeeded");
        return null!;
    }

    [TestMethod]
    public async Task A_Python_adapter_over_its_memory_limit_is_stopped_and_the_call_names_the_limit()
    {
        await using var host = await Start(await PythonPackage.Value, new LocalAdapterLimits { MemoryLimitBytes = Limit });

        Assert.AreEqual(Limit.ToString(), await host.CallAsync("Limit", null), "the SDK is handed the limit");

        var watch = Stopwatch.StartNew();
        var failure = await FailureOf(() => host.CallAsync("Allocate", 1000));
        Console.WriteLine($"{failure.GetType().Name} after {watch.Elapsed}: {failure.Message}");

        // On Linux RLIMIT_DATA stops the allocation inside the adapter (MemoryError); elsewhere the
        // watchdog kills it. Either way the caller is told about the limit, not about a broken pipe.
        StringAssert.Contains(failure.Message, "memory limit of 150 MB");
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), $"stopped after {watch.Elapsed}");
    }

    [TestMethod]
    public async Task Without_limits_a_Python_adapter_runs_as_it_always_has()
    {
        await using var host = await Start(await PythonPackage.Value, limits: null);

        Assert.AreEqual("none", await host.CallAsync("Limit", null), "no limit reaches the adapter");
        Assert.AreEqual("200", await host.CallAsync("Allocate", 200));

        // An empty limits object is no limits.
        await using var empty = await Start(await PythonPackage.Value, new LocalAdapterLimits());
        Assert.AreEqual("none", await empty.CallAsync("Limit", null));
    }

    [TestMethod]
    public async Task A_Python_adapter_over_its_CPU_limit_is_stopped_and_the_call_names_the_limit()
    {
        // One core flat out, against a ceiling of half a core.
        var halfACore = 50.0 / Environment.ProcessorCount;
        await using var host = await Start(await PythonPackage.Value, new LocalAdapterLimits
        {
            CpuPercentLimit = halfACore,
            CpuLimitSamples = 2,
            SampleInterval = TimeSpan.FromMilliseconds(500),
        });

        var watch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsExceptionAsync<Resident.AdapterStoppedException>(() => host.CallAsync("Spin", 20));
        Console.WriteLine($"after {watch.Elapsed}: {failure.Message}");

        StringAssert.Contains(failure.Message, "CPU limit");
        Assert.IsTrue(failure.LimitExceeded);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(15), $"stopped after {watch.Elapsed}");
    }

    [TestMethod]
    public async Task A_Node_adapter_over_its_memory_limit_is_stopped_and_the_call_names_the_limit()
    {
        var package = await BuildAsync("node", "main.js", NodeHog);
        await using var host = await Start(package, new LocalAdapterLimits { MemoryLimitBytes = Limit });

        var watch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsExceptionAsync<Resident.AdapterStoppedException>(() => host.CallAsync("Allocate", 1000));
        Console.WriteLine($"after {watch.Elapsed}: {failure.Message}");

        StringAssert.Contains(failure.Message, "memory limit of 150 MB");
        Assert.IsTrue(failure.LimitExceeded);
    }

    [TestMethod]
    public async Task The_conformance_kit_runs_an_adapter_under_its_limits()
    {
        var report = await new ConformanceRunner().RunAsync(new ConformanceOptions
        {
            PackageDirectory = await PythonPackage.Value,
            Settings = new Dictionary<string, string> { ["HogOnStart"] = "1" },
            CommandTimeoutSeconds = 30,
            Limits = new LocalAdapterLimits { MemoryLimitBytes = Limit },
        });
        Console.WriteLine(string.Join(Environment.NewLine, report.Checks.Select(c => $"{c.Outcome,-7} {c.Name} {c.Detail}")));

        var check = report.Checks.Single(c => c.Name == "an unknown command is refused");
        Assert.AreEqual(CheckOutcome.Failed, check.Outcome);
        StringAssert.Contains(check.Detail, "memory limit of 150 MB");
    }
}
