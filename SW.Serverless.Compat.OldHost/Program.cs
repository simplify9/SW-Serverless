using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SW.CloudFiles.Extensions;
using SW.PrimitiveTypes;
using SW.Serverless;

// Installs and runs one adapter the way a host on SimplyWorks.Serverless 10.0.0 does: by id, from
// storage, through IServerlessService. Everything printed for the test starts with a marker, so the
// adapter's own output cannot be mistaken for a result.
//
//   OldHost --bucket <name> --path <storage folder> --adapter <id or id/version>
//           [--command <name> --input <text>] [--expected yes] [--metadata yes] [--value <name=value>]...

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var values = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2)
{
    if (args[i] == "--value")
    {
        var pair = args[i + 1].Split('=', 2);
        values[pair[0]] = pair.Length > 1 ? pair[1] : "";
    }
    else options[args[i].TrimStart('-')] = args[i + 1];
}

var installed = Path.Combine(Path.GetTempPath(), "swsl-compat-oldhost", Guid.NewGuid().ToString("N"));

var services = new ServiceCollection();
// The registration extensions bind their options from configuration and throw without one.
services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
services.AddLogging(l => l.ClearProviders());
services.AddLocalTestsCloudFiles(o =>
{
    o.BucketName = options["bucket"];
    o.StoragePath = options["path"];
});
services.AddServerless(o =>
{
    o.AdapterRemotePath = "adapters";
    o.AdapterLocalPath = installed;
    o.AdapterMetadataCacheDuration = 1;
    o.CommandTimeout = 60;
});

using var provider = services.BuildServiceProvider();

try
{
    var adapterId = options["adapter"];

    if (options.ContainsKey("metadata"))
    {
        var metadata = await provider.GetRequiredService<AdapterInstaller>().GetMetadataAsync(adapterId);
        Console.WriteLine("METADATA:" + JsonConvert.SerializeObject(metadata.AdapterValues));
    }

    using (var scope = provider.CreateScope())
    {
        var serverless = scope.ServiceProvider.GetRequiredService<IServerlessService>();
        await serverless.StartAsync(adapterId, Guid.NewGuid().ToString("N"), values);

        if (options.ContainsKey("expected"))
            Console.WriteLine("EXPECTED:" + JsonConvert.SerializeObject(await serverless.GetExpectedStartupValues()));

        if (options.TryGetValue("command", out var command))
        {
            options.TryGetValue("input", out var input);
            Console.WriteLine("RESULT:" + await serverless.InvokeAsync<string>(command, input));
        }

        (serverless as IDisposable)?.Dispose();
    }

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("ERROR:" + ex.Message.Replace('\n', ' '));
    return 1;
}
finally
{
    try { Directory.Delete(installed, true); } catch { }
}
