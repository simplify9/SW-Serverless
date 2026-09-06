using SW.Serverless.Sdk;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.RabbitMq.Publisher
{
    static class Program
    {
        static Task Main() => Runner.RunResident(new PublisherHandler());
    }
}
