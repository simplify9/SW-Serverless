using System.Threading.Tasks;
using SW.Serverless.Sdk;

namespace SW.Serverless.UnitTests.GrpcClassicAdapter;

[AdapterKind("processor")]
[AdapterContract("orders", 1)]
public class Handler
{
    public Handler()
    {
        Runner.Expect("Prefix", "hello ");
        Runner.Expect("ApiKey", isPrivate: true, description: "The partner's key.");
    }

    public Task<string> Greet(string name) => Task.FromResult(Runner.StartupValueOf("Prefix") + name);

    public Task<string> Correlation() => Task.FromResult(Runner.StartupValueOf("CorrelationId"));

    public Task<int> Add(Numbers numbers) => Task.FromResult(numbers.A + numbers.B);

    public Task Fail() => throw new System.InvalidOperationException("asked to fail");
}

public class Numbers
{
    public int A { get; set; }
    public int B { get; set; }
}

static class Program
{
    static Task Main() => Runner.RunResident(new Handler());
}
