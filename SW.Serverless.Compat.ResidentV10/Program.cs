using SW.Serverless.Sdk;
using SW.Serverless.Sdk.Resident;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.Compat.ResidentV10
{
    [AdapterKind("receiver")]
    public class Handler : IResidentAdapter
    {
        IAdapterContext context;

        public Task StartAsync(IAdapterContext context, CancellationToken cancellationToken)
        {
            this.context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AdapterStatus> GetStatusAsync() =>
            Task.FromResult(new AdapterStatus { Connected = true, State = "Ready" });

        public Task<string> Echo(string input) => Task.FromResult($"{context.StartupValueOf("Prefix")}{input}");
    }

    static class Program
    {
        static Task Main() => Runner.RunResident(new Handler());
    }
}
