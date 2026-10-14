using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using GroceryErp.Data.Infrastructure;
using Microsoft.Extensions.Hosting;

namespace GroceryErp.App;

// One bounded, data-free histogram window per operation. Flush every thirty seconds so
// a store can inspect p50/p95/p99 without keeping raw SQL or customer values.
internal sealed class PerformanceRecorder(AppPaths paths) : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private MeterListener? _listener;
    private PeriodicTimer? _timer;
    private Task? _flushTask;
    private CancellationTokenSource? _stop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Directory.Exists(paths.LogsDir))
                foreach (string file in Directory.EnumerateFiles(paths.LogsDir, "performance-*.jsonl"))
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-30)) File.Delete(file);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name.StartsWith("GroceryErp.", StringComparison.Ordinal))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.Start();
        _stop = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        _flushTask = FlushPeriodicallyAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop?.Cancel();
        if (_flushTask is not null)
            try { await _flushTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        Flush();
    }

    private void Record(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string name = instrument;
        foreach (KeyValuePair<string, object?> tag in tags)
            if (tag.Key is "page" or "phase" or "operation" && tag.Value is string label) name += "." + label;
        _windows.GetOrAdd(name, static _ => new Window()).Add(value);
    }

    private async Task FlushPeriodicallyAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(cancellationToken))
                Flush();
        }
        catch (OperationCanceledException) { }
    }

    private void Flush()
    {
        _listener?.RecordObservableInstruments();
        var metrics = _windows.Select(pair => pair.Value.Snapshot(pair.Key))
            .OfType<MetricWindowSummary>().ToArray();
        if (metrics.Length == 0) return;
        if (metrics.Length == 1 && metrics[0].Name == "db.read.active") return;
        try
        {
            string file = Path.Combine(paths.LogsDir,
                "performance-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
            Directory.CreateDirectory(paths.LogsDir);
            File.AppendAllText(file, JsonSerializer.Serialize(new { utc = DateTime.UtcNow, metrics }) + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stop, null) is { } stop)
        {
            stop.Cancel();
            stop.Dispose();
        }
        Interlocked.Exchange(ref _timer, null)?.Dispose();
        Interlocked.Exchange(ref _listener, null)?.Dispose();
    }

    private sealed class Window
    {
        private const int Capacity = 4096;
        private readonly double[] _values = new double[Capacity];
        private long _count;

        public void Add(double value)
        {
            lock (_values)
            {
                _values[(int)(_count % Capacity)] = value;
                _count++;
            }
        }

        public MetricWindowSummary? Snapshot(string name)
        {
            double[] values;
            long count;
            lock (_values)
            {
                count = _count;
                if (count == 0) return null;
                values = _values.Take((int)Math.Min(count, Capacity)).Order().ToArray();
                _count = 0;
            }
            static double P(double[] sorted, double fraction) =>
                sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
            return new(name, count, values.Length, P(values, .5), P(values, .95), P(values, .99));
        }
    }

    private sealed record MetricWindowSummary(string Name, long Count, int Sampled,
        double P50, double P95, double P99);
}
