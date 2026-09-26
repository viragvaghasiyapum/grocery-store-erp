namespace GroceryErp.UI.Common.Modularity;

/// <summary>Describes a shell navigation destination contributed by a module.</summary>
public sealed record NavItem(
    string TitleKey,
    string Icon,
    Type PageType,
    int Order,
    string? BadgeProvider = null);

