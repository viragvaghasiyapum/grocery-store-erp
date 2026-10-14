using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GroceryErp.Data.Infrastructure;

// Metric names and tags contain operation names only, never SQL parameters or customer data.
public static class PerformanceMetrics
{
    public static readonly Meter Meter = new("GroceryErp.Performance");
    public static readonly Histogram<double> ReadQueueMilliseconds = Meter.CreateHistogram<double>("db.read.queue", "ms");
    public static readonly Histogram<double> ReadOpenMilliseconds = Meter.CreateHistogram<double>("db.read.open", "ms");
    public static readonly Histogram<double> ReadWorkMilliseconds = Meter.CreateHistogram<double>("db.read.work", "ms");
    public static readonly Histogram<double> ReportStageMilliseconds = Meter.CreateHistogram<double>("report.stage.duration", "ms");
    public static readonly Histogram<int> ReportStageRows = Meter.CreateHistogram<int>("report.stage.rows");
    public static readonly Histogram<double> TransactionMilliseconds = Meter.CreateHistogram<double>("db.write.transaction", "ms");
    public static readonly Histogram<long> WalBytes = Meter.CreateHistogram<long>("db.wal.bytes", "By");
    public static readonly Histogram<double> BackupMilliseconds = Meter.CreateHistogram<double>("backup.duration", "ms");
    public static readonly Histogram<double> MaintenanceMilliseconds = Meter.CreateHistogram<double>("maintenance.duration", "ms");
    public static readonly Histogram<double> CheckpointMilliseconds = Meter.CreateHistogram<double>("db.checkpoint.duration", "ms");
    public static readonly Counter<long> BusyRetries = Meter.CreateCounter<long>("db.busy.retries");
    public static readonly Histogram<double> PrintQueueMilliseconds = Meter.CreateHistogram<double>("print.queue.age", "ms");
    public static readonly Histogram<double> UiRefreshMilliseconds = Meter.CreateHistogram<double>("ui.refresh.duration", "ms");
    public static readonly Histogram<double> UiRenderQueueMilliseconds = Meter.CreateHistogram<double>("ui.render.queue", "ms");
    public static readonly Histogram<double> UiBindingAssignmentMilliseconds = Meter.CreateHistogram<double>("ui.binding.assignment", "ms");
    public static readonly Histogram<double> UiLayoutMilliseconds = Meter.CreateHistogram<double>("ui.layout.ready", "ms");
    public static readonly Histogram<double> UiFrameMilliseconds = Meter.CreateHistogram<double>("ui.frame.callback", "ms");
    public static readonly Histogram<double> UiFirstFrameTotalMilliseconds = Meter.CreateHistogram<double>("ui.first-frame.callback.total", "ms");

    public static IDisposable TrackMaintenance(string phase) => new MaintenanceScope(phase);

    private sealed class MaintenanceScope(string phase) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose() => MaintenanceMilliseconds.Record(
            Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            new KeyValuePair<string, object?>("phase", phase));
    }
}
