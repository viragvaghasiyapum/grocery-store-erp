using FlaUI.Core.AutomationElements;

namespace GroceryErp.UI.Tests;

/// <summary>
/// Activates an element through UI Automation instead of the mouse, so a long journey neither
/// moves the pointer nor clicks whatever window happens to be in front of the app.
/// </summary>
internal static class AutomationPress
{
    public static void Press(this AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported) element.Patterns.Invoke.Pattern.Invoke();
        else if (element.Patterns.SelectionItem.IsSupported) element.Patterns.SelectionItem.Pattern.Select();
        else element.Click();
    }
}
