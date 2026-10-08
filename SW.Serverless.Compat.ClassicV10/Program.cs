using SW.Serverless.Sdk;
using System.Threading.Tasks;

namespace SW.Serverless.Compat.ClassicV10
{
    [AdapterKind("handler")]
    public class Handler
    {
        public Handler()
        {
            Runner.Expect("Greeting", "hello");
            Runner.Expect("Token", isPrivate: true);
        }

        public Task<string> Echo(string input) => Task.FromResult(input);

        /// <summary>Reads a startup value, so a test sees values cross the classic wire too.</summary>
        public Task<string> Greet(string name) => Task.FromResult($"{Runner.StartupValueOf("Greeting")}, {name}");
    }

    static class Program
    {
        static Task Main() => Runner.Run(new Handler());
    }
}
