namespace GroceryErp.App.Presentation;

/// <summary>
/// Tracks checked rows of a keyset table. Rows can be picked on any page, or the selection can be
/// widened to every row matching the current filters, which the server resolves at review time.
/// Changing filters clears it, so a selection always belongs to the filters it was made under.
/// </summary>
internal sealed class TableSelection<TItem> where TItem : class
{
    private readonly Func<TItem, long> _id;
    private readonly Dictionary<long, TItem> _picked = [];

    public TableSelection(Func<TItem, long> id)
    {
        ArgumentNullException.ThrowIfNull(id);
        _id = id;
    }

    public event EventHandler? Changed;

    public bool AllMatching { get; private set; }

    public int MatchingCount { get; private set; }

    public int Count => AllMatching ? MatchingCount : _picked.Count;

    public bool IsEmpty => Count == 0;

    public IReadOnlyCollection<TItem> Picked => _picked.Values;

    public long[] PickedIds => [.. _picked.Keys];

    public bool IsSelected(TItem item) => AllMatching || _picked.ContainsKey(_id(item));

    public void Set(TItem item, bool selected)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (AllMatching)
        {
            // Unchecking one row of "all matching" falls back to an explicit pick.
            AllMatching = false;
            MatchingCount = 0;
        }
        if (selected) _picked[_id(item)] = item;
        else _picked.Remove(_id(item));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetPage(IEnumerable<TItem> rows, bool selected)
    {
        ArgumentNullException.ThrowIfNull(rows);
        AllMatching = false;
        MatchingCount = 0;
        foreach (TItem row in rows)
        {
            if (selected) _picked[_id(row)] = row;
            else _picked.Remove(_id(row));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Gets true, false or null (some) for the rows of the visible page.</summary>
    public bool? PageState(IReadOnlyCollection<TItem> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return false;
        if (AllMatching) return true;
        int selected = rows.Count(row => _picked.ContainsKey(_id(row)));
        return selected == 0 ? false : selected == rows.Count ? true : null;
    }

    public void SelectAllMatching(int count)
    {
        AllMatching = count > 0;
        MatchingCount = Math.Max(count, 0);
        _picked.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (!AllMatching && _picked.Count == 0) return;
        AllMatching = false;
        MatchingCount = 0;
        _picked.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces picked rows with their freshly loaded versions after a page reload.</summary>
    public void Refresh(IEnumerable<TItem> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (TItem row in rows)
            if (_picked.ContainsKey(_id(row))) _picked[_id(row)] = row;
    }
}
