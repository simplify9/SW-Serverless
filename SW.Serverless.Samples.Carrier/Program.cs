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
                services.AddGrpcClient<CarrierContract.Carrier.CarrierClient>(o =>
                    o.Address = new Uri(configuration["BaseUrl"] ?? "http://localhost:5200"));
            })
            .Build<CarrierHandler>()
            .RunResidentAsync();
    }
}
