using System.Threading.Tasks;
using SW.Serverless.Sdk;

namespace SW.Serverless.UnitTests.OrdersProcessor;

/// <summary>An order, as the tests' sample "orders" contract sends it.</summary>
public class Order
{
    public string OrderId { get; set; }
    public int Lines { get; set; }
}

/// <summary>
/// A processor for the sample "orders" contract, on the classic text protocol: accepts every order.
/// With Mode set to "broken" it answers without the Accepted every receipt must carry.
/// </summary>
[AdapterKind("processor")]
[AdapterContract("orders", 1)]
public class Processor
{
    public Processor()
    {
        Runner.Expect("Endpoint", "https://partner.example.test/orders", description: "Where orders go.");
        Runner.Expect("Mode", "working");
        Runner.Expect("ApiKey", isPrivate: true);
    }

    public Task<object> Process(Order order) =>
        Task.FromResult<object>(Runner.StartupValueOf("Mode") == "broken"
            ? new { Reference = "R-" + order.OrderId }
            : new { Accepted = true, Reference = "R-" + order.OrderId });
}

static class Program
{
    static Task Main() => Runner.Run(new Processor());
}
