using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace GroceryErp.App.Presentation;

/// <summary>
/// The shell's navigation. WPF UI records every page it creates in
/// <c>PageToNavigationItemDictionary</c> and never removes one; pages are not cached, so each
/// visit creates a new page and every page ever shown would stay alive with its table, rows and
/// bindings. Only the page on screen is kept.
/// </summary>
public sealed class ShellNavigationView : NavigationView
{
    private DispatcherOperation? _release;

    // WPF UI's template is an implicit style for its own type, which a subclass does not match.
    public ShellNavigationView() => SetResourceReference(StyleProperty, typeof(NavigationView));

    protected override void OnNavigated(object page)
    {
        base.OnNavigated(page);
        foreach (FrameworkElement shown in PageToNavigationItemDictionary.Keys
                     .Where(shown => !ReferenceEquals(shown, page)).ToArray())
            PageToNavigationItemDictionary.Remove(shown);
        // A page that has left holds native drawing memory until a full collection finalizes it, and
        // the small managed heap rarely asks for one: on the large-store journey the working set
        // climbed about 3 MiB a page until one came. Once the new page is idle, a background
        // collection, which does not pause the screen, gives that memory back.
        _release?.Abort();
        _release = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, static () =>
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false));
    }
}
