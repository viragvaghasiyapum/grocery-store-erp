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
    Money SellingPrice);

public sealed record PurchaseDraftInput(
    long VendorId,
    string? VendorInvoiceNumber,
    DateOnly? VendorInvoiceDate,
    DateOnly ReceivedDate,
    Money? VendorBillTotal,
    Money OtherCharges,
    string? Notes,
    IReadOnlyList<PurchaseLineInput> Lines);

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

public sealed record VendorLedgerItem(DateOnly Date, string Kind, string? Reference, Money Debit, Money Credit, Money Balance);

public sealed record VendorPaymentInput(long VendorId, long? PurchaseId, DateOnly Date, Money Amount, string Mode, string? Reference, string? Notes);

public sealed record PurchaseListItem(
    long Id,
    string? DocumentNumber,
    string VendorName,
    string? VendorInvoiceNumber,
    DateOnly ReceivedDate,
    string Status,
    Money GrandTotal,
    Money? VendorBillTotal);
