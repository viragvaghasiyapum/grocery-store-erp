using System.Reflection;
using System.Resources;

namespace GroceryErp.Localization;

/// <summary>Exposes the neutral and Gujarati shared application resources.</summary>
public static class SharedStrings
{
    /// <summary>Gets the resource manager for shared strings.</summary>
    public static ResourceManager ResourceManager { get; } = new(
        "GroceryErp.Localization.Resources.Strings",
        Assembly.GetExecutingAssembly());
}

