using SW.Serverless.Sdk;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.Ticker
{
    static class Program
    {
        // The ONLY difference from a classic adapter. Same zip, same install, same spawn.
        //   classic:  Runner.Run(new Handler())
        //   resident: Runner.RunResident(new Handler())
        static Task Main() => Runner.RunResident(new Handler());
    }
}
