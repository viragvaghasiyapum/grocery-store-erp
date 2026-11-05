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
    LotsDifferFromDocuments, RestoreProductUnavailable, RestoreProductTypeMismatch, RestoreExpiryRequired,
    SkuExists,
    // Rules an operator can trip from a screen, each with its own message. A guard that only an
    // invalid argument can trip keeps Validation and its general message.
    ProductArchived, InternalUseNotSaleable, LotBlocked, PriceLabelNeedsPrice, InvalidPhone, InvalidGstin,
    InvalidPan, CustomerPhoneExists, CategoryHierarchyInvalid, UnitFractionLocked, TaxRateOutOfRange,
    ProductClassLocked, LooseNeedsFractionalUnit, ProductUnitLocked, ExpiryRequired, TooManyRows,
    FilterRangeReversed, LotOnOpenBill, ExpiredReasonNeedsExpiredLot, CountLotDiffersFromDocuments,
    TaxRateInactive, TaxRateRequired, PurchaseLineTypeMismatch, ExpenseCategoryInactive,
    ExpenseLineAmountRequired, PurchaseTooManyLines, VendorReturnExceedsLot, SalaryAlreadyPosted,
    AssetAlreadyDisposed, AssetDisposalBeforeInUse, AssetDepreciationPosted, CloudFolderNotAllowed,
    DataFolderNotSeparate, DataFolderNotEmpty, DataMovePending, DataMoveStagingInUse, UsbDriveNotReady,
    PluExists, VendorGstinExists, NameExists, PrinterNotConfigured
}

public sealed class DomainException(ErrorCode code, params object[] arguments) : Exception(code.ToString())
{
    public ErrorCode Code { get; } = code;
    public IReadOnlyList<object> Arguments { get; } = arguments;
}
