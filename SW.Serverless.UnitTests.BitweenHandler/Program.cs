using System.Threading.Tasks;
using SW.Serverless.Sdk;

namespace SW.Serverless.UnitTests.BitweenHandler;

/// <summary>An exchange file as Bitween sends it.</summary>
public class ExchangeFile
{
    public string Data { get; set; }
    public string Filename { get; set; }
    public bool BadData { get; set; }
    public string ContentType { get; set; }
}

/// <summary>
/// A Bitween handler on the classic text protocol: delivers by echoing the order back. With Mode
/// set to "broken" it answers without the Data every exchange file must carry.
/// </summary>
[AdapterKind("handler")]
[AdapterContract("bitween", 1)]
public class Handler
{
    public Handler()
    {
        Runner.Expect("Endpoint", "https://partner.example.test/orders", description: "Where orders go.");
        Runner.Expect("Mode", "working");
        Runner.Expect("ApiKey", isPrivate: true);
    }

    public Task<object> Handle(ExchangeFile file) =>
        Task.FromResult<object>(Runner.StartupValueOf("Mode") == "broken"
            ? new { Filename = "answer.json" }
            : new ExchangeFile { Data = "{\"accepted\":true}", Filename = "answer.json", ContentType = "application/json" });
}

static class Program
{
    static Task Main() => Runner.Run(new Handler());
}
