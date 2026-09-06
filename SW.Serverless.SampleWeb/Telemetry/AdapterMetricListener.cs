using Microsoft.Extensions.Hosting;
using SW.Serverless.Resident;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Serverless.SampleWeb.Telemetry
{
    /// <summary>
    /// Adapter metric frames are republished on System.Diagnostics.Metrics by the runtime, so they
    /// export through whatever the host already uses. This listens on that same meter — the
    /// dashboard is just one more consumer, not a special case.
    /// </summary>
    public class AdapterMetricListener : IHostedService
    {
        readonly DashboardState state;
        MeterListener listener;

        public AdapterMetricListener(DashboardState state) => this.state = state;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == AdapterMetrics.Meter.Name)
                        l.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                var adapterId = "";
                foreach (var tag in tags)
                    if (tag.Key == "adapter.id") adapterId = tag.Value?.ToString() ?? "";
                state.RecordMetric(instrument.Name, adapterId, value);
            });

            listener.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            listener?.Dispose();
            return Task.CompletedTask;
        }
    }
}
