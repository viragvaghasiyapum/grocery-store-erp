using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using GroceryErp.Localization;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Re-renders text a view set in code (counts, summaries, "All …" filter items, converted cells)
/// when the UI language changes. Text bound in XAML follows on its own. The view listens only
/// while it is loaded, so a closed page is never kept alive by the language source.
/// </summary>
internal static class LanguageRefresh
{
    public static void Attach(FrameworkElement view, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(refresh);
        void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Item[]") view.Dispatcher.BeginInvoke(refresh, DispatcherPriority.Background);
        }
        view.Loaded += (_, _) =>
        {
            TranslationSource.Instance.PropertyChanged -= Changed;
            TranslationSource.Instance.PropertyChanged += Changed;
        };
        view.Unloaded += (_, _) => TranslationSource.Instance.PropertyChanged -= Changed;
    }
}
