using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SW.Serverless.Sdk.Hosting;
using System;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.Carrier
{
    static class Program
    {
        static Task Main() => AdapterHost.CreateBuilder()
            .ConfigureServices((configuration, services) =>
            {
                services.Configure<CarrierOptions>(configuration);
                services.AddSingleton<CallLog>();

                // A pooled, long-lived HTTP/2 channel. Under the classic lifecycle this would be
                // dialled and thrown away on every single call; here it is opened once and reused
                // for the life of the adapter, which is most of where the latency saving comes from.
                // Set BaseUrl to an https endpoint against a real carrier: account number, API key
                // and address data all cross this channel, and h2c sends them in the clear. The
                // plain-http default exists only so the bundled local simulator works out of the box.
                var baseUrl = new Uri(configuration["BaseUrl"] ?? "http://localhost:5200");
                if (!baseUrl.IsLoopback && baseUrl.Scheme != Uri.UriSchemeHttps)
                    Console.Error.WriteLine(
                        $"WARNING: carrier BaseUrl '{baseUrl}' is not TLS; credentials would be sent in the clear.");

                services.AddGrpcClient<CarrierContract.Carrier.CarrierClient>(o => o.Address = baseUrl);
            })
            .Build<CarrierHandler>()
            .RunResidentAsync();
    }
}
