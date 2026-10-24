namespace GroceryErp.App.Presentation;

internal enum PageMove { First, Next, Previous, Same }

/// <summary>Keyset cursor for lists ordered by name, then ID.</summary>
internal sealed record NameCursor(string Name, long Id);

/// <summary>Keyset cursor for lists ordered by date descending, then ID descending.</summary>
internal sealed record DateCursor(DateOnly Date, long Id);

/// <summary>
/// Remembers the cursors of earlier keyset pages so Previous returns to exactly the rows seen.
/// A move is committed only after its page loaded, so a failed or stale load never shifts it.
/// </summary>
internal sealed class KeysetPager<TCursor>
{
    private readonly Stack<TCursor?> _previous = new();

    public TCursor? Current { get; private set; }

    public TCursor? NextCursor { get; private set; }

    public bool HasMore { get; private set; }

    public bool CanGoBack => _previous.Count > 0;

    public int PageNumber => _previous.Count + 1;

    /// <summary>Gets the cursor that loads the page for <paramref name="move"/>.</summary>
    public TCursor? CursorFor(PageMove move) => move switch
    {
        PageMove.Next => NextCursor,
        PageMove.Previous => _previous.Count > 0 ? _previous.Peek() : default,
        PageMove.Same => Current,
        _ => default,
    };

    public bool CanMove(PageMove move) => move switch
    {
        PageMove.Next => HasMore,
        PageMove.Previous => _previous.Count > 0,
        _ => true,
    };

    public void Commit(PageMove move, bool hasMore, TCursor? nextCursor)
    {
        switch (move)
        {
            case PageMove.First:
                _previous.Clear();
                Current = default;
                break;
            case PageMove.Next:
                _previous.Push(Current);
                Current = NextCursor;
                break;
            case PageMove.Previous:
                Current = _previous.Count > 0 ? _previous.Pop() : default;
                break;
        }
        HasMore = hasMore;
        NextCursor = nextCursor;
    }

    public void Reset()
    {
        _previous.Clear();
        Current = default;
        NextCursor = default;
        HasMore = false;
    }
}
