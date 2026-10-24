using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Sizes a keyset page to the rows that fit the visible table, so a page never needs an inner
/// scroll bar and never leaves empty space. A resize reloads only after it settles.
/// </summary>
internal sealed class FitPageSize
{
    internal const int Minimum = 5;
    internal const int Maximum = 100;
    private readonly DataGrid _grid;
    private readonly int _fallback;
    private readonly DispatcherTimer _settle;
    private int _applied;

    public FitPageSize(DataGrid grid, int fallback = 30)
    {
        ArgumentNullException.ThrowIfNull(grid);
        _grid = grid;
        _fallback = Math.Clamp(fallback, Minimum, Maximum);
        _settle = new DispatcherTimer(DispatcherPriority.Background, grid.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(250) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            // Only a table that has loaded a page reloads for a new size.
            if (_applied > 0 && Measure() is int rows && rows != _applied) RowsChanged?.Invoke(this, EventArgs.Empty);
        };
        grid.SizeChanged += (_, args) =>
        {
            if (Math.Abs(args.NewSize.Height - args.PreviousSize.Height) < 1) return;
            _settle.Stop();
            _settle.Start();
        };
        grid.Unloaded += (_, _) => _settle.Stop();
    }

    /// <summary>Raised after a settled resize changes how many rows fit.</summary>
    public event EventHandler? RowsChanged;

    /// <summary>Gets the row count for the next query and remembers it as applied.</summary>
    public int Take()
    {
        _applied = Measure() ?? (_applied > 0 ? _applied : _fallback);
        return _applied;
    }

    private int? Measure()
    {
        if (!_grid.IsLoaded) return null;
        // A filter row that has just folded changes the table height; settle layout before measuring.
        _grid.UpdateLayout();
        if (_grid.ActualHeight <= 0) return null;
        double header = double.IsNaN(_grid.ColumnHeaderHeight) ? 32 : _grid.ColumnHeaderHeight;
        double row = double.IsNaN(_grid.RowHeight) || _grid.RowHeight <= 0 ? 40 : _grid.RowHeight;
        // Leave room for the borders and a horizontal scroll bar on narrow windows.
        double available = _grid.ActualHeight - header - _grid.BorderThickness.Top - _grid.BorderThickness.Bottom
            - SystemParameters.HorizontalScrollBarHeight;
        return Math.Clamp((int)Math.Floor(available / row), Minimum, Maximum);
    }
}
