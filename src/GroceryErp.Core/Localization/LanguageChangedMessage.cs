namespace GroceryErp.Core.Localization;

/// <summary>Signals that presentation text must be refreshed after a UI-language change.</summary>
public sealed record LanguageChangedMessage(string CultureName);

