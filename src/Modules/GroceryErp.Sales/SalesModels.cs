using GroceryErp.Core.Numerics;
using GroceryErp.Inventory;

namespace GroceryErp.Sales;

public enum SaleStatus { Draft, Held, Completed, PartiallyRefunded, Refunded, Void }
public enum PaymentMode { Cash, Upi, Card, Other }
public enum RefundMode { Cash, Upi, Card, CreditNote, Other }

public sealed record PaymentInput(PaymentMode Mode, Money Amount, string? Reference = null);

public sealed record CompleteSaleCommand(
    long SaleId,
    IReadOnlyList<PaymentInput> Payments,
    string OperationId);

public sealed record CompletedSale(
    long SaleId,
    string InvoiceNumber,
    PricedBill Totals,
    Money Change,
    bool WasReplay = false);

public sealed record SaleLine(
    long Id,
    int LineNumber,
    long ProductId,
    long LotId,
    string ProductName,
    string? ProductNameLocal,
    string? BatchNumber,
    DateOnly? ExpiryDate,
    Quantity Quantity,
    Money UnitPrice,
    Money? Mrp,
    Money Discount,
    Percent TaxRate,
    Money Taxable,
    Money Tax,
    Money Total,
    Money UnitCost,
    Money Cost,
    string? ScannedBarcode,
    Money LineDiscount = default,
    Quantity StockOnHand = default)
{
    public string? Sku { get; init; }
    public string? Unit { get; init; }
    public string? ProductClass { get; init; }
    public string? LotCode { get; init; }
    public string? SnapshotProvenance { get; init; }
    public string? LegacyUnknownFields { get; init; }

    /// <summary>Gets the name of the tax charged on the sale as it was at completion; none when no tax was charged.</summary>
    public string? TaxLabel { get; init; }

    /// <summary>Gets one piece's purchase tax, copied from the lot; null for a line saved before tax snapshots.</summary>
    public Money? UnitCostTax { get; init; }

    /// <summary>Gets one piece's purchase price including tax, copied from the lot.</summary>
    public Money? UnitCostInclTax { get; init; }

    /// <summary>Gets one piece's purchase price before tax.</summary>
    public Money? PurchasePrice => UnitCostInclTax is { } incl && UnitCostTax is { } tax ? incl - tax : null;

    /// <summary>Gets the tax charged as shown, so Before tax + Tax charged = Total on screen.</summary>
    public decimal TaxChargedShown => TwoDecimals.Remainder(Total, Taxable);

    public string SkuDisplay => Sku ??
        (SnapshotProvenance == "LEGACY_SALE_LINE" ? "Legacy unknown" : "—");
    public string BatchDisplay => BatchNumber ??
        (LegacyUnknownFields?.Contains('B') == true ? "Legacy unknown" : "—");
    public string ExpiryDisplay => ExpiryDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ??
        (LegacyUnknownFields?.Contains('E') == true ? "Legacy unknown" : "—");
}

public sealed record SaleDetails(
    long Id,
    string? InvoiceNumber,
    DateOnly SaleDate,
    SaleStatus Status,
    long? CustomerId,
    string? CustomerName,
    string? HoldLabel,
    Money BillDiscount,
    IReadOnlyList<SaleLine> Lines,
    PricedBill Totals,
    IReadOnlyList<PaymentInput> Payments,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? VoidReason)
{
    /// <summary>The change given back from the cash handed over; zero when none was.</summary>
    public Money Change { get; init; }
}

public sealed record HeldSale(long Id, DateTime CreatedAt, string? Label, int ItemCount, Money Total)
{
    public bool IsHeld { get; init; } = true;
}

public sealed record SavedBillPage(IReadOnlyList<HeldSale> Items, bool HasMore,
    string? NextCreatedAt, long? NextId);

public sealed record HeldBillReviewLine(long LineId, string ProductName, Quantity Quantity,
    Money PreviousPrice, Money CurrentPrice, Money? PreviousMrp, Money? CurrentMrp,
    Money CurrentCost, Quantity Available, bool IsActive, DateOnly? ExpiryDate)
{
    public bool PriceChanged => PreviousPrice != CurrentPrice || PreviousMrp != CurrentMrp;
}

public sealed record HeldBillReview(long SaleId, IReadOnlyList<HeldBillReviewLine> Lines);

public sealed record ReturnableSaleLine(SaleLine Line, Quantity Remaining)
{
    public Quantity AlreadyReturned => Line.Quantity - Remaining;
    public Money Refundable { get; init; }
    public Money RemainingRefund { get; init; }
    public bool OriginalLotAvailable { get; init; }
    // The lot that would take the units back has expired, so they can only be damaged.
    public bool ResaleExpired { get; init; }
}

public sealed record CustomerItem(long Id, string Name, string? Phone, string? Gstin);

public sealed record SaleHistoryFilter(
    DateOnly? From = null,
    DateOnly? To = null,
    SaleStatus? Status = null,
    PaymentMode? PaymentMode = null,
    Money? MinimumAmount = null,
    Money? MaximumAmount = null,
    string? InvoiceNumber = null,
    DateOnly? BeforeDate = null,
    long? BeforeId = null,
    int Limit = 100,
    string? Product = null,
    string? Customer = null);

public sealed record SaleHistoryItem(
    long Id,
    string? InvoiceNumber,
    DateOnly SaleDate,
    SaleStatus Status,
    int LineCount,
    Money GrandTotal,
    string PaymentModes,
    DateTime? CompletedAt,
    string? VoidReason);

public sealed record SaleReturnHistoryItem(string ReturnNumber, DateOnly Date, string ProductName,
    Quantity Quantity, Money Refund, bool Resellable, string Reason)
{
    public string? RestockLotCode { get; init; }
    public string DispositionCode { get; init; } = "RETURN_ORIGINAL";
}

public sealed record SaleActivityItem(DateOnly Date, string ProductName, string LotCode,
    string Type, Quantity Quantity);

public sealed record SaleHistoryActivity(IReadOnlyList<SaleReturnHistoryItem> Returns,
    IReadOnlyList<SaleActivityItem> Events);

/// <summary>A returned line. <see cref="Restore"/> is the operator's product for resold units whose product was deleted.</summary>
public sealed record ReturnLineInput(long SaleLineId, Quantity Quantity, bool Resellable,
    RestoreProductChoice? Restore = null);

public sealed record CreateReturnCommand(
    long SaleId,
    DateOnly ReturnDate,
    string Reason,
    RefundMode RefundMode,
    IReadOnlyList<ReturnLineInput> Lines,
    string OperationId);

public sealed record CompletedReturn(
    long ReturnId,
    string ReturnNumber,
    Money Taxable,
    Money Tax,
    Money Refund,
    Money Cost,
    bool WasReplay = false);

public sealed record PaymentTotal(PaymentMode Mode, Money Amount);

public sealed record DayEndSummary(
    DateOnly Date,
    int BillCount,
    Money Gross,
    Money Discounts,
    Money Net,
    Money Tax,
    Money RoundOff,
    IReadOnlyList<PaymentTotal> Payments,
    int VoidCount,
    Money VoidValue,
    int ReturnCount,
    Money ReturnValue,
    Money CashExpenses,
    Money CashExpected,
    Money CashChange = default,
    Money CashRefunds = default,
    Money Payable = default);

public sealed record ReceiptLine(
    string Name,
    string? LocalName,
    Quantity Quantity,
    Money UnitPrice,
    Money? Mrp,
    Money Discount,
    Money Taxable,
    Money Tax,
    Money Total,
    Percent TaxRate);

public sealed record ReceiptData(
    string StoreName,
    string StoreAddress,
    string StorePhone,
    string StoreGstin,
    string TaxMode,
    string InvoiceNumber,
    DateTime CompletedAt,
    string? CustomerName,
    IReadOnlyList<ReceiptLine> Lines,
    Money Gross,
    Money Discount,
    Money Taxable,
    Money Tax,
    Money RoundOff,
    Money GrandTotal,
    Money Savings,
    IReadOnlyList<PaymentInput> Payments,
    Money Change,
    string PrintLanguage,
    string Footer,
    bool Duplicate,
    string? OriginalInvoiceNumber = null,
    string? ReturnReason = null,
    string? RefundMethod = null,
    int WidthDots = 0,
    bool? BilingualNames = null);
