namespace GroceryErp.Core.Errors;

public enum ErrorCode
{
    Unknown, NotFound, Validation, InsufficientStock, NoLotForProduct, ExpiredLot,
    FractionalQtyNotAllowed, SaleFinalized, PurchaseFinalized,
    LotProductMismatch, PriceAboveMrp, DuplicateVendorBill, InvalidDate, PeriodLocked,
    BarcodeNotFound, BarcodeExists, InvalidBarcode, EmptyBill, PaymentShort,
    OverpaymentNonCash, CreditNeedsCustomer, DiscountExceedsLine, ReturnQtyExceedsSold,
    VoidNotAllowed, CancelNotAllowed, DatabaseNewerThanApp, ClockBehindLastBill,
    WrongPassword, BackupInvalid, StockCountReviewChanged, DatabaseBusy, DiskFull, DatabaseCorrupt,
    RestoreRecovered, DatabaseIo, AnnualReportInvalid, EntryChanged, EntryVoided, EffectiveMonthClosed,
    AssetTermsFinalized, DamageProductDeleted, CompactionFailed, VendorReturnProductDeleted,
    LotsDifferFromDocuments
}

public sealed class DomainException(ErrorCode code, params object[] arguments) : Exception(code.ToString())
{
    public ErrorCode Code { get; } = code;
    public IReadOnlyList<object> Arguments { get; } = arguments;
}
