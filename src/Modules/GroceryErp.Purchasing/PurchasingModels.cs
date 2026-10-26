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

/// <summary>
/// One purchase line as typed. <see cref="TaxRateId"/> is the tax rate chosen for the line; the
/// purchase saves its percent and name from <c>tax_rate</c> when the draft is saved and again when
/// it is posted. The selling price and MRP include tax.
/// </summary>
public sealed record PurchaseLineInput(
    long ProductId,
    string? BatchNumber,
    DateOnly? ManufacturingDate,
    DateOnly? ExpiryDate,
    Quantity Quantity,
    Quantity FreeQuantity,
    Money Rate,
    Money Discount,
    long? TaxRateId,
    Money? Mrp,
    Money SellingPrice)
{
    public string LineKind { get; init; } = "PRODUCT";
    public long? ExpenseCategoryId { get; init; }

    /// <summary>
    /// Converts a line typed from a bill whose rate and discount include tax into the before-tax
    /// rate and discount the purchase stores. The discount never exceeds the converted line value.
    /// </summary>
    public PurchaseLineInput ExcludingTax(Percent taxRate)
    {
        Money rate = Tax.FromInclusive(Rate, taxRate).Taxable;
        Money discount = Tax.FromInclusive(Discount, taxRate).Taxable;
        Money gross = rate * Quantity;
        return this with { Rate = rate, Discount = discount > gross ? gross : discount };
    }
}

public sealed record PurchaseDraftInput(
    long VendorId,
    string? VendorInvoiceNumber,
    DateOnly? VendorInvoiceDate,
    DateOnly ReceivedDate,
    Money? VendorBillTotal,
    Money OtherCharges,
    string? Notes,
    IReadOnlyList<PurchaseLineInput> Lines)
{
    /// <summary>
    /// Gets whether the lines' rates and discounts were typed including GST, as the bill prints
    /// them; otherwise they exclude GST.
    /// </summary>
    public bool RatesIncludeTax { get; init; }
}

public sealed record PurchaseDraft(long Id, PurchaseDraftInput Input);

/// <summary>A new line's starting values: the product's tax rate (ID and percent) and the vendor's last before-tax rate.</summary>
public sealed record PurchaseLineDefaults(Money? LastRate, long? TaxRateId, Percent TaxRate, Money? Mrp,
    Money? SellingPrice, DateOnly? SuggestedExpiry, int ExpiryAlertDays);

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

    /// <summary>Gets the tax rate's name as it was when the line was posted.</summary>
    public string? TaxLabel { get; init; }

    /// <summary>Gets one piece's tax; null for a line posted before tax snapshots.</summary>
    public Money? UnitTax { get; init; }

    /// <summary>Gets one piece's purchase price including tax and its share of other charges.</summary>
    public Money? UnitCostInclTax { get; init; }

    /// <summary>Gets one piece's purchase price before tax, with its share of other charges.</summary>
    public Money? PurchasePrice => UnitCostInclTax is { } incl && UnitTax is { } tax ? incl - tax : null;

    /// <summary>Gets the tax per piece as shown, so the shown prices add up.</summary>
    public decimal? UnitTaxShown => UnitCostInclTax is { } incl && PurchasePrice is { } before
        ? TwoDecimals.Remainder(incl, before) : null;
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

/// <summary>
/// Server-side purchase filters. Status: DRAFT, POSTED or CANCELLED (shown as Void). LineKind:
/// PRODUCT, INTERNAL or EXPENSE. DocumentQuery matches the GRN number exactly or a vendor invoice
/// number prefix. ReturnableOnly keeps posted purchases with stock left on one of their own lots.
/// </summary>
public sealed record PurchaseFilter
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public long? VendorId { get; init; }
    public string? ProductQuery { get; init; }
    public string? Status { get; init; }
    public string? LineKind { get; init; }
    public string? DocumentQuery { get; init; }
    public bool ReturnableOnly { get; init; }
}

/// <summary>Vendor filters. Status: ACTIVE, INACTIVE or ALL. Query matches name, phone or GSTIN.</summary>
public sealed record VendorFilter
{
    public string? Query { get; init; }
    public string Status { get; init; } = "ACTIVE";
    public bool WithBalanceOnly { get; init; }
}

public sealed record VendorPage(IReadOnlyList<VendorItem> Items, bool HasMore, string? NextName, long? NextId);

/// <summary>A row's eligibility for a purchasing bulk action, resolved before anything changes.</summary>
public sealed record PurchasingBulkRow(long Id, string Label, bool Eligible, string? ReasonKey, Money Amount);
