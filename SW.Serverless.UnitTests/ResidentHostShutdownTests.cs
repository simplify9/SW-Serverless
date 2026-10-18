using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SW.Serverless.Resident;
using System;
using System.Threading.Tasks;

namespace SW.Serverless.UnitTests
{
    /// <summary>
    /// An adapter that exits while the host is stopping. The exit is handled on the process's own
    /// callback, and the host can stop and dispose its token between that handler checking it is
    /// still running and scheduling the restart. The restart then threw ObjectDisposedException on
    /// a thread nothing catches, and that took the whole process down.
    /// </summary>
    [TestClass]
    public class ResidentHostShutdownTests
    {
        [TestMethod]
        public async Task A_restart_scheduled_after_the_host_stopped_is_dropped()
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var host = new ResidentAdapterHost(new ResidentOptions(), null, null, null, loggerFactory);
            await host.DisposeAsync();

            var supervised = ResidentAdapterHost.Supervised.For("late-exit",
                new AdapterSpec { AdapterId = "late.exit", InstanceKey = "late-exit" });

            host.ScheduleRestart(supervised, TimeSpan.Zero);

            Assert.IsNull(supervised.PendingRestart, "a stopped host must not schedule a restart");
        }
    }
}
