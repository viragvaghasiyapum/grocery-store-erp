using GroceryErp.Core.Numerics;

namespace GroceryErp.Inventory;

public sealed record LotItem(
    long Id,
    long ProductId,
    string ProductName,
    string LotCode,
    string? BatchNumber,
    DateOnly ReceivedDate,
    DateOnly? ExpiryDate,
    Money UnitCost,
    Money? Mrp,
    Money SellingPrice,
    Quantity Received,
    Quantity OnHand,
    bool IsActive,
    string ExpiryBucketCode = "NONE")
{
    public string OriginKind { get; init; } = "DIRECT";
    public long DetailVersion { get; init; }

    /// <summary>Gets the name of the tax rate the lot was bought at, as it was then.</summary>
    public string? TaxLabel { get; init; }

    /// <summary>Gets one piece's purchase tax; null for a lot saved before tax snapshots.</summary>
    public Money? UnitTax { get; init; }

    /// <summary>Gets one piece's purchase price including tax; null for a lot saved before tax snapshots.</summary>
    public Money? UnitCostInclTax { get; init; }

    /// <summary>Gets one piece's purchase price before tax.</summary>
    public Money? PurchasePrice => UnitCostInclTax is { } incl && UnitTax is { } tax ? incl - tax : null;

    /// <summary>Gets the tax per piece as shown, so the shown prices add up.</summary>
    public decimal? UnitTaxShown => UnitCostInclTax is { } incl && PurchasePrice is { } before
        ? TwoDecimals.Remainder(incl, before) : null;
}

public sealed record LotDetailsEdit(long LotId, long ExpectedVersion, Money SellingPrice,
    Money? Mrp, DateOnly? ExpiryDate);

public sealed record LotDeletionPreview(long LotId, string LotCode, Quantity OnHand,
    bool CanDelete, string? BlockReason);

/// <summary>Server-side lot filters. Status: ALL, AVAILABLE, ZERO or BLOCKED. Expiry: ALL, EXPIRED, EXPIRING or NONE.</summary>
public sealed record LotFilter
{
    public long? ProductId { get; init; }

    /// <summary>Gets text matched against the batch, lot code or product name.</summary>
    public string? Query { get; init; }

    public string Status { get; init; } = "ALL";

    public string Expiry { get; init; } = "ALL";

    public DateOnly? ExpiryFrom { get; init; }

    public DateOnly? ExpiryTo { get; init; }

    /// <summary>Gets PURCHASE_LINE, DIRECT or CUSTOMER_RETURN, or null for every source.</summary>
    public string? Source { get; init; }

    public Money? MinPrice { get; init; }

    public Money? MaxPrice { get; init; }

    public DateOnly? AsOf { get; init; }
}

public enum LotBulkAction { Block, Unblock, DeleteZero }

/// <summary>A lot's eligibility for a bulk action, resolved before anything changes.</summary>
public sealed record LotBulkRow(long Id, string Label, bool Eligible, string? ReasonKey, Quantity OnHand, Money CostValue);

public sealed record LotPage(IReadOnlyList<LotItem> Items, bool HasMore, long? NextId);

/// <summary>A direct or opening lot. The purchase price is one piece's price before tax.</summary>
public sealed record OpeningStockLine(
    long ProductId,
    Quantity Quantity,
    Money PurchasePrice,
    Money SellingPrice,
    Money? Mrp,
    DateOnly? ExpiryDate,
    string? BatchNumber,
    DateOnly ReceivedDate)
{
    /// <summary>Gets the tax rate the lot was bought at, or null for the product's rate.</summary>
    public long? TaxRateId { get; init; }
}
