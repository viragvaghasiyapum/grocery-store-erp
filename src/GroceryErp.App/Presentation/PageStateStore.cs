using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Keeps each table page's filters for the session. Navigation creates a new page instance,
/// so a page saves its filter record when it unloads and restores it when it is created again.
/// </summary>
public sealed class PageStateStore
{
    private readonly Dictionary<string, object> _states = new(StringComparer.Ordinal);

    internal static PageStateStore? Current =>
        (Application.Current as App)?.ServicesOrNull?.GetService<PageStateStore>();

    public T? Get<T>(string key) where T : class =>
        _states.TryGetValue(key, out object? state) ? state as T : null;

    public void Set<T>(string key, T state) where T : class
    {
        ArgumentNullException.ThrowIfNull(state);
        _states[key] = state;
    }
}
