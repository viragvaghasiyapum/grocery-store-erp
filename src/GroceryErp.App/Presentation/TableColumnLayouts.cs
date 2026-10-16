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
    static TableColumnLayouts() { }

    private static readonly string LayoutPath = ResolveLayoutPath();
    private static readonly Dictionary<string, double[]> Saved = Load();
    private static readonly ConditionalWeakTable<DataGrid, GridState> States = new();
    private static readonly DependencyPropertyDescriptor WidthDescriptor =
        DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn))!;

    internal static void Register()
    {
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
        grid.Unloaded += state.OnUnloaded;
        Window.GetWindow(grid)?.Closing += state.OnWindowClosing;
        state.AttachColumns();
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

    private static string Key(DataGrid grid, string owner)
    {
        string columns = string.Join("|", grid.Columns.Select((column, index) =>
        {
            string binding = column is DataGridBoundColumn { Binding: Binding field }
                ? field.Path?.Path ?? string.Empty : string.Empty;
            return $"{index}:{column.GetType().Name}:{binding}";
        }));
        return $"{owner}/{grid.Name}/{columns}";
    }

    private static Dictionary<string, double[]> Load()
    {
        try
        {
            return File.Exists(LayoutPath)
                ? JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(LayoutPath)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
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

    private sealed class GridState(DataGrid grid, string owner)
    {
        private readonly Window? _window = Window.GetWindow(grid);
        private readonly HashSet<DataGridColumn> _attached = [];
        private readonly DispatcherTimer _saveTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450),
        };
        private bool _restoring;
        private bool _dirty;

        public void AttachColumns()
        {
            foreach (DataGridColumn column in grid.Columns)
                if (_attached.Add(column)) WidthDescriptor.AddValueChanged(column, OnWidthChanged);
            _saveTimer.Tick -= OnSaveTick;
            _saveTimer.Tick += OnSaveTick;
        }

        public void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _saveTimer.Stop();
            _dirty = false;
            if (e.OldItems is not null)
                foreach (DataGridColumn column in e.OldItems)
                    if (_attached.Remove(column)) WidthDescriptor.RemoveValueChanged(column, OnWidthChanged);
            AttachColumns();
            grid.Dispatcher.BeginInvoke(Restore, DispatcherPriority.Loaded);
        }

        public void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _saveTimer.Stop();
            if (_dirty) Save();
            grid.Columns.CollectionChanged -= OnColumnsChanged;
            grid.Unloaded -= OnUnloaded;
            if (_window is not null) _window.Closing -= OnWindowClosing;
            foreach (DataGridColumn column in _attached)
                WidthDescriptor.RemoveValueChanged(column, OnWidthChanged);
            _attached.Clear();
            States.Remove(grid);
        }

        public void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            _saveTimer.Stop();
            if (_dirty) Save();
        }

        public void Restore()
        {
            string key = Key(grid, owner);
            if (!Saved.TryGetValue(key, out double[]? widths) || widths.Length != grid.Columns.Count) return;
            _restoring = true;
            try
            {
                for (int index = 0; index < widths.Length; index++)
                    if (double.IsFinite(widths[index]) && widths[index] >= 24)
                        grid.Columns[index].Width = new DataGridLength(widths[index], DataGridLengthUnitType.Pixel);
            }
            finally { _restoring = false; }
        }

        private void OnWidthChanged(object? sender, EventArgs e)
        {
            if (_restoring) return;
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void OnSaveTick(object? sender, EventArgs e)
        {
            _saveTimer.Stop();
            Save();
        }

        private void Save()
        {
            double[] widths = grid.Columns.Select(column => column.ActualWidth).ToArray();
            if (widths.Length == 0 || widths.Any(width => !double.IsFinite(width) || width < 24)) return;
            Saved[Key(grid, owner)] = widths;
            Write();
            _dirty = false;
        }

        public void SaveNow()
        {
            _saveTimer.Stop();
            Save();
        }
    }
}
