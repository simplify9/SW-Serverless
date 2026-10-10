using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SW.Serverless.Sdk;

namespace SW.Serverless.UnitTests.OrdersSource;

public class Order
{
    public string OrderId { get; set; }
    public int Lines { get; set; }
}

/// <summary>
/// A source for the sample "orders" contract, over gRPC: reads the order files in a folder and
/// removes one when told to — so a test can see whether Remove was called.
/// </summary>
[AdapterKind("source")]
[AdapterContract("orders", 1)]
public class Source
{
    public Source() => Runner.Expect("Folder");

    string Folder => Runner.StartupValueOf("Folder");

    public Task Open() => Task.CompletedTask;

    public Task<IEnumerable<string>> List() =>
        Task.FromResult<IEnumerable<string>>(Directory.GetFiles(Folder).Select(Path.GetFileName).OrderBy(n => n).ToList());

    public Task<Order> Fetch(string orderId) =>
        Task.FromResult(new Order { OrderId = orderId, Lines = File.ReadAllText(Path.Combine(Folder, orderId)).Length });

    public Task Remove(string orderId)
    {
        File.Delete(Path.Combine(Folder, orderId));
        return Task.CompletedTask;
    }

    public Task Close() => Task.CompletedTask;
}

static class Program
{
    static Task Main() => Runner.RunResident(new Source());
}
