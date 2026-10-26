using GroceryErp.Core.Numerics;

namespace GroceryErp.Catalog;

public sealed record ProductBarcodeInput(
    string Barcode,
    Quantity PackQuantity,
    string Symbology = "EAN13",
    bool IsPrimary = false,
    string Source = "MANUFACTURER");

public sealed record ProductInput(
    string Sku,
    string Name,
    string? NameLocal,
    string? SearchTerms,
    long CategoryId,
    long? BrandId,
    long UomId,
    string? HsnCode,
    long? TaxRateId,
    bool IsLoose,
    string? PluCode,
    bool TrackExpiry,
    int? DefaultShelfLifeDays,
    int? ExpiryAlertDays,
    Quantity ReorderLevel,
    Money? DefaultSellingPrice,
    Money? DefaultMrp,
    IReadOnlyCollection<ProductBarcodeInput> Barcodes)
{
    public string ProductClass { get; init; } = "SALEABLE";
}

/// <summary>
/// A new product's first lot. The purchase price is one piece's price before tax; the selling price
/// and MRP include tax, as every price does.
/// </summary>
public sealed record NewProductLotInput(Quantity Quantity, Money PurchasePrice,
    Money SellingPrice, Money? Mrp, string? BatchNumber, DateOnly? ExpiryDate,
    DateOnly ReceivedDate, string Reason)
{
    /// <summary>Gets the tax rate the lot was bought at, or null for the product's rate.</summary>
    public long? TaxRateId { get; init; }
}

public sealed class ProductListItem
{
    public long Id { get; init; }
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? NameLocal { get; init; }
    public string Category { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public string Uom { get; init; } = string.Empty;
    public long StockScaled { get; init; }
    public long? SellingPriceScaled { get; init; }
    public long? MrpScaled { get; init; }

    /// <summary>Gets the next lot to sell's purchase price including tax, the lot its price and MRP come from.</summary>
    public long? CostInclTaxScaled { get; init; }
    public bool IsLoose { get; init; }
    public bool IsActive { get; init; }
    public bool HasBarcode { get; init; }
    public string ProductClass { get; init; } = "SALEABLE";
    public bool HasExpiredStock { get; init; }
    public Quantity Stock => Quantity.FromScaled(StockScaled);
    public Money? SellingPrice => SellingPriceScaled is null ? null : Money.FromScaled(SellingPriceScaled.Value);
    public Money? Mrp => MrpScaled is null ? null : Money.FromScaled(MrpScaled.Value);
    public Money? CostInclTax => CostInclTaxScaled is null ? null : Money.FromScaled(CostInclTaxScaled.Value);
}

public sealed record ProductSearchFilter(
    string? Query = null,
    long? CategoryId = null,
    long? BrandId = null,
    bool LowStockOnly = false,
    bool NoBarcodeOnly = false,
    bool MissingLocalNameOnly = false,
    bool IncludeArchived = false,
    bool LooseOnly = false,
    int Limit = 200,
    bool SaleableOnly = false,
    string ExpiryFilter = "ALL",
    DateOnly? ExpiryAsOf = null)
{
    /// <summary>Gets only archived products; implies <see cref="IncludeArchived"/>.</summary>
    public bool ArchivedOnly { get; init; }

    /// <summary>Gets SALEABLE or INTERNAL to limit the classification, or null for both.</summary>
    public string? ProductClass { get; init; }

    public bool HasStockOnly { get; init; }

    /// <summary>Gets products with at least one lot received from this vendor.</summary>
    public long? VendorId { get; init; }

    /// <summary>Puts an exact SKU or barcode match ahead of the name-ordered results.</summary>
    public bool ExactFirst { get; init; }
}

public sealed record ProductSearchPage(
    IReadOnlyList<ProductListItem> Items,
    bool HasMore,
    string? NextName,
    long? NextId);

public sealed class MasterItem
{
    public long Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? NameLocal { get; init; }
    public bool IsActive { get; init; }
    public long? ParentId { get; init; }
    public int? ExpiryAlertDays { get; init; }
}

public sealed class UnitItem
{
    public long Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? NameLocal { get; init; }
    public bool AllowFraction { get; init; }
}

public sealed record TaxRateItem(long Id, string Name, decimal Rate, bool IsActive);

public sealed record ProductDetails(long Id, ProductInput Product, bool IsActive);

public sealed record BarcodeOwner(long ProductId, string ProductName);

public sealed record CategoryInput(string Name, string? NameLocal, long? ParentId, int? ExpiryAlertDays);

public enum ProductRemoval { Deleted, Archived }

public enum CatalogBulkAction { Archive, Restore, Category, Tax, Classification, Labels, Barcode }

/// <summary>One bulk action and its target value (category, tax rate, classification or barcode prefix).</summary>
public sealed record CatalogBulkCommand(CatalogBulkAction Action, long? TargetId = null, string? TargetCode = null);

/// <summary>A product's eligibility for a bulk action, resolved on the server before anything changes.</summary>
/// <param name="SaleLines">For a category change, the product's sale lines, which bound the report-day
/// work the change triggers; 0 for other actions.</param>
public sealed record CatalogBulkRow(long Id, string Sku, string Name, bool Eligible, string? ReasonKey,
    Quantity Quantity, Money CostValue, long SaleLines = 0);

public sealed record CatalogBulkPreview(int Selected, IReadOnlyList<long> EligibleIds,
    int Skipped, Quantity AffectedQuantity, Money AffectedCost)
{
    public IReadOnlyList<CatalogBulkRow> Rows { get; init; } = [];
}
