using SW.Serverless.Sdk;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.Greedy
{
    static class Program
    {
        static Task Main() => Runner.RunResident(new GreedyHandler());
    }
}
