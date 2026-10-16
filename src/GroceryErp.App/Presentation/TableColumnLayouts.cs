using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace GroceryErp.App.Presentation;

internal static class TableColumnLayouts
{
    private static readonly string LayoutPath = ResolveLayoutPath();
    private static readonly Dictionary<string, ColumnLayout> Saved = Load();
    private static readonly ConditionalWeakTable<DataGrid, GridState> States = new();
    private static readonly DependencyPropertyDescriptor WidthDescriptor =
        DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn))!;
    private static bool _registered;

    internal static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(DataGrid), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded), true);
        EventManager.RegisterClassHandler(typeof(DataGrid), Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(OnColumnResizeCompleted), true);
    }

    private static string ResolveLayoutPath()
    {
        string? testRoot = Environment.GetEnvironmentVariable("GROCERY_ERP_TEST_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot) &&
            Path.GetFullPath(testRoot).StartsWith(Path.GetFullPath(Path.GetTempPath()),
                StringComparison.OrdinalIgnoreCase))
            return Path.Combine(testRoot, "table-columns.json");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hare Krushna", "Grocery ERP", "table-columns.json");
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid) Attach(grid);
    }

    private static void OnColumnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is DataGrid grid) Attach(grid, restore: false)?.SaveNow();
    }

    private static GridState? Attach(DataGrid grid, bool restore = true)
    {
        if (string.IsNullOrWhiteSpace(grid.Name)) return null;
        if (States.TryGetValue(grid, out GridState? existing)) return existing;
        var state = new GridState(grid, OwnerName(grid));
        States.Add(grid, state);
        grid.Columns.CollectionChanged += state.OnColumnsChanged;
        grid.ColumnDisplayIndexChanged += state.OnDisplayIndexChanged;
        grid.ColumnReordered += state.OnColumnReordered;
        grid.Unloaded += state.OnUnloaded;
        state.ObserveColumns();
        if (restore) state.Restore();
        return state;
    }

    private static string OwnerName(DependencyObject grid)
    {
        for (DependencyObject? current = grid; current is not null; current = Parent(current))
            if (current is Page page) return page.GetType().FullName ?? page.GetType().Name;
            else if (current is Window window) return window.GetType().FullName ?? window.GetType().Name;
        return "Table";
    }

    private static DependencyObject? Parent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);

    private static string Key(DataGrid grid, string owner) => $"{owner}/{grid.Name}";

    private static string LegacyKey(DataGrid grid, string owner)
    {
        // The collection order remains stable when the user changes DisplayIndex.
        string columns = string.Join("|", grid.Columns.Select((column, index) =>
        {
            string binding = column is DataGridBoundColumn { Binding: Binding field }
                ? field.Path?.Path ?? string.Empty : string.Empty;
            return $"{index}:{column.GetType().Name}:{binding}";
        }));
        return $"{Key(grid, owner)}/{columns}";
    }

    private static ColumnLayout? FindLayout(DataGrid grid, string owner)
    {
        string key = Key(grid, owner);
        if (Saved.TryGetValue(key, out ColumnLayout? layout)) return layout;
        if (Saved.TryGetValue(LegacyKey(grid, owner), out layout)) return layout;

        // Older releases included column implementation types in the key. A template
        // changing to a bound column should not erase the user's saved layout.
        string prefix = key + "/";
        return Saved.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                                    entry.Value.Widths?.Length == grid.Columns.Count)
            .OrderByDescending(entry => LegacyMatchScore(entry.Key, grid))
            .Select(entry => entry.Value)
            .FirstOrDefault();
    }

    private static int LegacyMatchScore(string key, DataGrid grid)
    {
        int score = 0;
        foreach (var (column, index) in grid.Columns.Select((column, index) => (column, index)))
        {
            string binding = column is DataGridBoundColumn { Binding: Binding field }
                ? field.Path?.Path ?? string.Empty : string.Empty;
            if (binding.Length > 0 && key.Contains($"{index}:{column.GetType().Name}:{binding}", StringComparison.Ordinal))
                score += 2;
            else if (binding.Length > 0 && key.Contains($":{binding}|", StringComparison.Ordinal))
                score++;
        }
        return score;
    }

    private static Dictionary<string, ColumnLayout> Load()
    {
        try
        {
            if (!File.Exists(LayoutPath)) return [];
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(LayoutPath));
            var layouts = new Dictionary<string, ColumnLayout>();
            foreach (JsonProperty entry in document.RootElement.EnumerateObject())
            {
                ColumnLayout? layout = entry.Value.ValueKind switch
                {
                    JsonValueKind.Array => new ColumnLayout
                    {
                        Widths = (JsonSerializer.Deserialize<double[]>(entry.Value.GetRawText()) ?? [])
                            .Select(static width => (double?)width).ToArray()
                    },
                    JsonValueKind.Object => JsonSerializer.Deserialize<ColumnLayout>(entry.Value.GetRawText()),
                    _ => null
                };
                if (layout is not null) layouts[entry.Name] = layout;
            }
            return layouts;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            string temporaryPath = LayoutPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Saved));
            File.Move(temporaryPath, LayoutPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only profile should not interrupt table use.
        }
    }

    private sealed class ColumnLayout
    {
        public double?[] Widths { get; set; } = [];
        public int[] DisplayIndexes { get; set; } = [];
    }

    private sealed class GridState(DataGrid grid, string owner)
    {
        private bool _restoring;
        private bool _pendingRestore;
        private bool _dirty;
        private readonly HashSet<DataGridColumn> _observedColumns = [];
        private DispatcherTimer? _saveTimer;

        public void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _pendingRestore = true;
            ObserveColumns();
            grid.Dispatcher.BeginInvoke(() =>
            {
                _pendingRestore = false;
                Restore();
            }, DispatcherPriority.Loaded);
        }

        public void OnDisplayIndexChanged(object? sender, DataGridColumnEventArgs e) => ScheduleSave();

        public void OnColumnReordered(object? sender, DataGridColumnEventArgs e) => SaveNow();

        public void ObserveColumns()
        {
            foreach (DataGridColumn column in _observedColumns.Where(column => !grid.Columns.Contains(column)).ToArray())
            {
                WidthDescriptor.RemoveValueChanged(column, OnWidthChanged);
                _observedColumns.Remove(column);
            }
            foreach (DataGridColumn column in grid.Columns)
                if (_observedColumns.Add(column)) WidthDescriptor.AddValueChanged(column, OnWidthChanged);
        }

        private void OnWidthChanged(object? sender, EventArgs e) => ScheduleSave();

        private void ScheduleSave()
        {
            if (_restoring || _pendingRestore) return;
            _dirty = true;
            if (_saveTimer is null)
            {
                _saveTimer = new DispatcherTimer(DispatcherPriority.Background, grid.Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(280) };
                _saveTimer.Tick += (_, _) =>
                {
                    _saveTimer.Stop();
                    if (_dirty) SaveNow();
                };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_dirty) SaveNow();
            _saveTimer?.Stop();
            foreach (DataGridColumn column in _observedColumns)
                WidthDescriptor.RemoveValueChanged(column, OnWidthChanged);
            _observedColumns.Clear();
            grid.Columns.CollectionChanged -= OnColumnsChanged;
            grid.ColumnDisplayIndexChanged -= OnDisplayIndexChanged;
            grid.ColumnReordered -= OnColumnReordered;
            grid.Unloaded -= OnUnloaded;
            States.Remove(grid);
        }

        public void Restore()
        {
            if (grid.Columns.Count == 0 ||
                FindLayout(grid, owner) is not { } layout ||
                layout.Widths is null || layout.Widths.Length != grid.Columns.Count) return;
            _restoring = true;
            try
            {
                for (int index = 0; index < layout.Widths.Length; index++)
                    if (layout.Widths[index] is { } width && double.IsFinite(width) && width >= 24)
                        grid.Columns[index].Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);

                int[] order = layout.DisplayIndexes ?? [];
                if (order.Length == grid.Columns.Count &&
                    order.Order().SequenceEqual(Enumerable.Range(0, order.Length)))
                    try
                    {
                        foreach (int index in Enumerable.Range(0, order.Length).OrderBy(index => order[index]))
                            grid.Columns[index].DisplayIndex = order[index];
                    }
                    catch (InvalidOperationException)
                    {
                        // Frozen-column rules may have changed since the layout was saved.
                    }
            }
            finally { _restoring = false; }
        }

        public void SaveNow()
        {
            if (_restoring || _pendingRestore || grid.Columns.Count == 0) return;
            grid.UpdateLayout();
            double?[] widths = grid.Columns.Select(column =>
                double.IsFinite(column.ActualWidth) && column.ActualWidth >= 24
                    ? (double?)column.ActualWidth : null).ToArray();
            if (widths.All(static width => width is null)) return;
            int[] order = grid.Columns.Select(static column => column.DisplayIndex).ToArray();
            if (!order.Order().SequenceEqual(Enumerable.Range(0, order.Length))) return;
            string key = Key(grid, owner);
            foreach (string legacyKey in Saved.Keys.Where(savedKey =>
                         savedKey.StartsWith(key + "/", StringComparison.Ordinal)).ToArray())
                Saved.Remove(legacyKey);
            Saved[key] = new ColumnLayout { Widths = widths, DisplayIndexes = order };
            _dirty = false;
            Write();
        }
    }
}
