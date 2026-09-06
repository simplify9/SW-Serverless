using SW.Serverless.Sdk;
using System.Threading.Tasks;

namespace SW.Serverless.Samples.Classic
{
    static class Program
    {
        // The classic lifecycle: Run, not RunResident. The host starts this process, issues one
        // or more commands, and the process dies when the caller's scope ends.
        //
        // Nothing in this project references protocol 2, and its cloud metadata carries no
        // Protocol key — so the host takes the v1 code path for it, byte for byte. That is what
        // keeps the existing published fleet working.
        static Task Main() => Runner.Run(new Handler());
    }
}
