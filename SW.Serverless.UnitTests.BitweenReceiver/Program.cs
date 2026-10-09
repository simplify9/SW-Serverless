using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SW.Serverless.Sdk;

namespace SW.Serverless.UnitTests.BitweenReceiver;

public class ExchangeFile
{
    public string Data { get; set; }
    public string Filename { get; set; }
}

/// <summary>
/// A Bitween receiver over gRPC that reads the files in a folder and deletes one when told to —
/// so a test can see whether DeleteFile was called.
/// </summary>
[AdapterKind("receiver")]
[AdapterContract("bitween", 1)]
public class Handler
{
    public Handler() => Runner.Expect("Folder");

    string Folder => Runner.StartupValueOf("Folder");

    public Task Initialize() => Task.CompletedTask;

    public Task<IEnumerable<string>> ListFiles() =>
        Task.FromResult<IEnumerable<string>>(Directory.GetFiles(Folder).Select(Path.GetFileName).OrderBy(n => n).ToList());

    public Task<ExchangeFile> GetFile(string fileId) =>
        Task.FromResult(new ExchangeFile { Data = File.ReadAllText(Path.Combine(Folder, fileId)), Filename = fileId });

    public Task DeleteFile(string fileId)
    {
        File.Delete(Path.Combine(Folder, fileId));
        return Task.CompletedTask;
    }

    public Task Finalize() => Task.CompletedTask;
}

static class Program
{
    static Task Main() => Runner.RunResident(new Handler());
}
