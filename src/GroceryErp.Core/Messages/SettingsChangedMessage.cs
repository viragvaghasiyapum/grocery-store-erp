namespace GroceryErp.Core.Messages;

public sealed record SettingsChangedMessage(IReadOnlyCollection<string> Keys);
