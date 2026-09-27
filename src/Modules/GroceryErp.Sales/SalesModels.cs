using GroceryErp.Core.Numerics;

namespace GroceryErp.Sales;

public enum SaleStatus { Draft, Held, Completed, Void }
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
    string? ScannedBarcode);

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
    string? VoidReason);

public sealed record HeldSale(long Id, DateTime CreatedAt, string? Label, int ItemCount, Money Total);

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
    int Limit = 100);

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

public sealed record ReturnLineInput(long SaleLineId, Quantity Quantity, bool Resellable);
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
    Money CashExpected);

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
    bool Duplicate);
