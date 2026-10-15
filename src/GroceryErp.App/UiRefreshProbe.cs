using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.App;

internal sealed class UiRefreshProbe(string page, FrameworkElement? visual = null, long? searchStarted = null) : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    public string? SearchShape { get; set; }

    public static UiRefreshProbe Track(string page, FrameworkElement? visual = null, long? searchStarted = null) =>
        new(page, visual, searchStarted);

    // Measures the synchronous ItemsSource/control update separately from the DB await.
    public void Bind(Action assign) => RecordBinding(page, assign);

    public static void RecordBinding(string page, Action assign)
    {
        long started = Stopwatch.GetTimestamp();
        try { assign(); }
        finally
        {
            PerformanceMetrics.UiBindingAssignmentMilliseconds.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("page", page));
        }
    }

    public void Dispose()
    {
        long assigned = Stopwatch.GetTimestamp();
        var tag = new KeyValuePair<string, object?>("page", page);
        PerformanceMetrics.UiRefreshMilliseconds.Record(
            Stopwatch.GetElapsedTime(_started, assigned).TotalMilliseconds, tag);
        if (Application.Current?.Dispatcher is not { } dispatcher || !dispatcher.CheckAccess()) return;
        dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            PerformanceMetrics.UiRenderQueueMilliseconds.Record(
                Stopwatch.GetElapsedTime(assigned).TotalMilliseconds, tag));
        if (visual is not { IsLoaded: true }) return;

        EventHandler? layout = null;
        EventHandler? frame = null;
        var timeout = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        { Interval = TimeSpan.FromSeconds(5) };
        void Stop()
        {
            if (layout is not null) visual.LayoutUpdated -= layout;
            if (frame is not null) CompositionTarget.Rendering -= frame;
            timeout.Stop();
        }
        bool layoutSeen = false;
        bool frameSeen = false;
        layout = (_, _) =>
        {
            if (layoutSeen) return;
            layoutSeen = true;
            PerformanceMetrics.UiLayoutMilliseconds.Record(
                Stopwatch.GetElapsedTime(assigned).TotalMilliseconds, tag);
            visual.LayoutUpdated -= layout;
            if (frameSeen) Stop();
        };
        frame = (_, _) =>
        {
            if (frameSeen) return;
            frameSeen = true;
            PerformanceMetrics.UiFrameMilliseconds.Record(
                Stopwatch.GetElapsedTime(assigned).TotalMilliseconds, tag);
            PerformanceMetrics.UiFirstFrameTotalMilliseconds.Record(
                Stopwatch.GetElapsedTime(_started).TotalMilliseconds, tag);
            if (searchStarted is long started && SearchShape is string shape)
                PerformanceMetrics.UiSearchVisibleMilliseconds.Record(
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    tag, new KeyValuePair<string, object?>("phase", shape));
            CompositionTarget.Rendering -= frame;
            if (layoutSeen) Stop();
        };
        timeout.Tick += (_, _) => Stop();
        visual.LayoutUpdated += layout;
        CompositionTarget.Rendering += frame;
        timeout.Start();
    }
}
