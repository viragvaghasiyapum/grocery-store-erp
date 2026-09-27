namespace GroceryErp.Core.Messages;

public sealed record StockChangedMessage(IReadOnlyCollection<long> ProductIds);
