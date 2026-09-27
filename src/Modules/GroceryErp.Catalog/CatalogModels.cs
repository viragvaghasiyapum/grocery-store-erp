using GroceryErp.Core.Numerics;

namespace GroceryErp.Catalog;

public sealed record ProductBarcodeInput(
    string Barcode,
    Quantity PackQuantity,
    string Symbology = "EAN13",
    bool IsPrimary = false);

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
    bool PriceIncludesTax,
    bool IsLoose,
    string? PluCode,
    bool TrackExpiry,
    int? DefaultShelfLifeDays,
    int? ExpiryAlertDays,
    Quantity ReorderLevel,
    Money? DefaultSellingPrice,
    Money? DefaultMrp,
    IReadOnlyCollection<ProductBarcodeInput> Barcodes);

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
    public bool IsLoose { get; init; }
    public bool IsActive { get; init; }
    public bool HasBarcode { get; init; }
    public Quantity Stock => Quantity.FromScaled(StockScaled);
    public Money? SellingPrice => SellingPriceScaled is null ? null : Money.FromScaled(SellingPriceScaled.Value);
    public Money? Mrp => MrpScaled is null ? null : Money.FromScaled(MrpScaled.Value);
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
    int Limit = 200);

public sealed class MasterItem
{
    public long Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? NameLocal { get; init; }
    public bool IsActive { get; init; }
    public long? ParentId { get; init; }
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

public sealed record CategoryInput(string Name, string? NameLocal, long? ParentId, int? ExpiryAlertDays);

public enum ProductRemoval { Deleted, Archived }
