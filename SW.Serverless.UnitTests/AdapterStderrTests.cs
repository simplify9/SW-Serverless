using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.PrimitiveTypes;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// An adapter dotnet will not start - built for a runtime that is not installed, or packaged
    /// without its files. dotnet says why on stderr and exits before the SDK runs, so the line
    /// has no marker. The host used to drop it, which left the call failing with "Received null
    /// data." and the reason nowhere at all.
    /// </summary>
    [TestClass]
    public class AdapterStderrTests
    {
        [TestMethod]
        public async Task What_dotnet_prints_about_an_adapter_it_cannot_start_is_logged()
        {
            var directory = Directory.CreateTempSubdirectory();
            var adapterPath = Path.Combine(directory.FullName, "broken.adapter.dll");
            await File.WriteAllTextAsync(adapterPath, "not an assembly");

            var log = new WarningLog();
            using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(log));
            var serverless = new ServerlessService(new ServerlessOptions(), new MemoryCache(new MemoryCacheOptions()),
                loggerFactory, new ServiceCollection().BuildServiceProvider());

            try
            {
                await serverless.StartAsync("broken.adapter", Guid.NewGuid().ToString("N"), adapterPath);
                await Assert.ThrowsExceptionAsync<Exception>(() => serverless.InvokeAsync("Handle", null));
            }
            catch (IOException)
            {
                // Writing the command can also fail outright, when the process is already gone.
            }
            finally
            {
                ((IDisposable)serverless).Dispose();
            }

            // stderr is read on its own thread, so the line can land after the call has failed.
            for (var i = 0; i < 50 && log.Lines.IsEmpty; i++) await Task.Delay(100);

            Assert.IsTrue(log.Lines.Any(l => l.Category == "serverless.adapters.broken.adapter"),
                "dotnet's reason for not starting the adapter was not logged");
            directory.Delete(true);
        }

        sealed class WarningLog : ILoggerProvider
        {
            public ConcurrentQueue<(string Category, string Message)> Lines { get; } = new();

            public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
            public void Dispose() { }

            sealed class Logger(WarningLog log, string category) : ILogger
            {
                public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                    Func<TState, Exception, string> formatter)
                {
                    if (logLevel == LogLevel.Warning) log.Lines.Enqueue((category, formatter(state, exception)));
                }
            }
        }
    }
}
