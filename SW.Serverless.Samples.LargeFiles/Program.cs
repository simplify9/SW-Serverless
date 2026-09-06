using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SW.Serverless.Sdk.Hosting;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.LargeFiles
{
    static class Program
    {
        // Dependency injection, configuration binding and ILogger — the same shape as any .NET
        // service. The container is built only after startup values arrive, so injecting
        // IOptions<StreamOptions> into a constructor is safe here in a way that reading a
        // startup value in a plain handler constructor is not.
        static Task Main() => AdapterHost.CreateBuilder()
            .ConfigureServices((configuration, services) =>
            {
                services.Configure<StreamOptions>(configuration);
                services.AddSingleton<IChunkReader, ChunkReader>();
                services.AddSingleton<ThroughputMeter>();
            })
            .Build<LargeFileHandler>()
            .RunResidentAsync();
    }
}
