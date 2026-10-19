using GroceryErp.Core.Numerics;

namespace GroceryErp.Purchasing;

public sealed record VendorInput(
    string Name,
    string? ContactPerson,
    string? Phone,
    string? AlternatePhone,
    string? Email,
    string? Address,
    string? City,
    string? State,
    string? Pincode,
    string? Gstin,
    string? Pan,
    string? UpiId,
    string? BankDetails,
    int PaymentTermsDays,
    Money OpeningBalance,
    string? Notes);

public sealed record VendorItem(long Id, string Name, string? Phone, string? Gstin, Money BalanceDue, bool IsActive);

public sealed record PurchaseLineInput(
    long ProductId,
    string? BatchNumber,
    DateOnly? ManufacturingDate,
    DateOnly? ExpiryDate,
    Quantity Quantity,
    Quantity FreeQuantity,
    Money Rate,
    Money Discount,
    Percent TaxRate,
    Money? Mrp,
    Money SellingPrice)
{
    public string LineKind { get; init; } = "PRODUCT";
    public long? ExpenseCategoryId { get; init; }
}

public sealed record PurchaseDraftInput(
    long VendorId,
    string? VendorInvoiceNumber,
    DateOnly? VendorInvoiceDate,
    DateOnly ReceivedDate,
    Money? VendorBillTotal,
    Money OtherCharges,
    string? Notes,
    IReadOnlyList<PurchaseLineInput> Lines);

public sealed record PurchaseDraft(long Id, PurchaseDraftInput Input);

public sealed record PurchaseLineDefaults(Money? LastRate, Percent TaxRate, Money? Mrp,
    Money? SellingPrice, DateOnly? SuggestedExpiry, int ExpiryAlertDays, bool PriceIncludesTax);

public sealed record PurchaseTotals(
    Money Subtotal,
    Money Discount,
    Money Tax,
    Money OtherCharges,
    Money RoundOff,
    Money GrandTotal,
    Money? VendorBillTotal)
{
    public Money? Difference => VendorBillTotal is null ? null : VendorBillTotal.Value - GrandTotal;
}

public sealed record PostedPurchase(long Id, string DocumentNumber, PurchaseTotals Totals, IReadOnlyList<long> LotIds, bool WasReplay = false);

public sealed record PurchaseInvoiceLine(int LineNumber, string Sku, string ProductName,
    string? BatchNumber, DateOnly? ManufacturingDate, DateOnly? ExpiryDate,
    Quantity Quantity, Quantity FreeQuantity, Money Rate, Money Discount,
    Percent TaxRate, Money TaxAmount, Money LineTotal, Money LandedUnitCost,
    Money? Mrp, Money SellingPrice)
{
    public string LineKind { get; init; } = "PRODUCT";
    public string? ExpenseCategoryName { get; init; }
    public string? SnapshotProvenance { get; init; }
    public string? LegacyUnknownFields { get; init; }
}

public sealed record PurchaseInvoice(long Id, string? DocumentNumber, string VendorName,
    string? VendorInvoiceNumber, DateOnly? VendorInvoiceDate, DateOnly ReceivedDate,
    string Status, string? Notes, PurchaseTotals Totals, IReadOnlyList<PurchaseInvoiceLine> Lines)
{
    public string? TaxMode { get; init; }
}

public sealed record VendorLedgerItem(DateOnly Date, string Kind, string? Reference, Money Debit, Money Credit, Money Balance);
public sealed record VendorLedgerPage(IReadOnlyList<VendorLedgerItem> Items, bool HasMore,
    DateOnly? NextDate, string? NextKind, long? NextId);

public sealed record VendorPaymentInput(long VendorId, long? PurchaseId, DateOnly Date, Money Amount, string Mode, string? Reference, string? Notes);

public sealed record VendorPurchaseChoice(long Id, string Label);
public sealed record VendorPurchasePage(IReadOnlyList<VendorPurchaseChoice> Items, bool HasMore,
    DateOnly? NextDate, long? NextId);

public sealed record VendorLotItem(string ProductName, string LotCode, string? BatchNumber,
    DateOnly ReceivedDate, DateOnly? ExpiryDate, Quantity OnHand);
public sealed record VendorLotPage(IReadOnlyList<VendorLotItem> Items, bool HasMore,
    DateOnly? NextDate, long? NextId);

public sealed record VendorReturnLot(long LotId, long ProductId, string ProductName, string LotCode,
    Quantity Received, Quantity OnHand, Quantity Returnable, Money OriginalBeforeTax,
    Money OriginalTax, Money CreditedBeforeTax, Money CreditedTax, Money UnitCost,
    bool AllowFraction, Quantity AlreadyReturned);

public sealed record VendorReturnLineInput(long LotId, Quantity Quantity, Money CreditBeforeTax, Money TaxCredit);

public sealed record VendorReturnInput(long PurchaseId, DateOnly Date, string VendorCreditNumber,
    string Reason, IReadOnlyList<VendorReturnLineInput> Lines);

public sealed record PostedVendorReturn(long Id, string DocumentNumber, Money CreditTotal,
    Money CostTotal, bool WasReplay = false);

public sealed record VendorReturnHistoryItem(long Id, string DocumentNumber, DateOnly Date,
    Money CreditTotal, Money CostTotal, bool IsCancelled, DateOnly? CancellationDate);
public sealed record VendorReturnHistoryPage(IReadOnlyList<VendorReturnHistoryItem> Items,
    bool HasMore, long? NextId);

public sealed record PurchaseListItem(
    long Id,
    string? DocumentNumber,
    string VendorName,
    string? VendorInvoiceNumber,
    DateOnly ReceivedDate,
    string Status,
    Money GrandTotal,
    Money? VendorBillTotal);

public sealed record PurchasePage(IReadOnlyList<PurchaseListItem> Items, bool HasMore,
    DateOnly? NextDate, long? NextId);
