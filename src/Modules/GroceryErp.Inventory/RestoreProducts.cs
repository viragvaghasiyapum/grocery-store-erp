using System.Data;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Inventory;

/// <summary>
/// What a document saved about units being restored after their product was deleted: the details
/// a new product starts from and what an existing product must match. A null field was not saved.
/// </summary>
/// <param name="Key">The record the choice is for: a sale line, vendor-return line or damage.</param>
/// <param name="DocumentNumber">The document the units come back from, as the lot's reason names it.</param>
/// <param name="Quantity">The units being restored, in the original product's unit.</param>
/// <param name="UnitCost">The booked cost of one unit; the new lot keeps it.</param>
public sealed record RestoreDraft(
    long Key, string DocumentNumber, string ProductName, string? ProductNameLocal, string SuggestedSku,
    long? CategoryId, long? UnitId, string? UnitCode, long? TaxRateId, string? HsnCode,
    bool? TrackExpiry, bool? IsLoose, string ProductClass,
    string? BatchNumber, DateOnly? ExpiryDate, Quantity Quantity, Money UnitCost,
    Money? SellingPrice, Money? Mrp);

/// <summary>A product created for restored units from the details the operator reviewed.</summary>
public sealed record RestoreNewProduct(string Sku, string Name, string? NameLocal, long CategoryId,
    long UnitId, long? TaxRateId, string? HsnCode, bool TrackExpiry, bool IsLoose);

/// <summary>
/// The operator's choice for units whose product was deleted: an existing product or a new one,
/// and the new lot's prices, which include tax. <see cref="ExpiryDate"/> is used only when the
/// original record saved none.
/// </summary>
public sealed record RestoreProductChoice(long? ExistingProductId, RestoreNewProduct? NewProduct,
    Money SellingPrice, Money? Mrp, DateOnly? ExpiryDate = null);

/// <summary>The product restored units go into and the terms of their new lot.</summary>
public sealed record RestoredProduct(long ProductId, Money SellingPrice, Money? Mrp, DateOnly? ExpiryDate,
    bool Created);

public sealed record RestoreOption(long Id, string Label, long? RateScaled = null, bool AllowFraction = false,
    string? Code = null);

public sealed record RestoreOptions(IReadOnlyList<RestoreOption> Categories, IReadOnlyList<RestoreOption> Units,
    IReadOnlyList<RestoreOption> TaxRates);

// Units come back from a customer return, a cancelled vendor return or a damage correction. When
// their product was deleted, the operator picks an active product of the same type whose unit
// takes the quantity, or creates one; quantity, booked cost and the P&L effect never change.
public static class RestoreProducts
{
    public static RestoreOptions Options(IDbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // Dapper builds a positional record only through a constructor whose parameters match the
        // columns exactly, and RestoreOption's optional parameters do not count, so each row is read
        // as a tuple and the option is built here.
        return new(
            connection.Query<(long Id, string Label)>(
                    "SELECT id,name FROM category WHERE is_active=1 ORDER BY name,id")
                .Select(static row => new RestoreOption(row.Id, row.Label)).ToArray(),
            connection.Query<(long Id, string Label, bool AllowFraction, string Code)>(
                    "SELECT id,code||' — '||name,allow_fraction,code FROM uom ORDER BY code,id")
                .Select(static row => new RestoreOption(row.Id, row.Label, AllowFraction: row.AllowFraction, Code: row.Code))
                .ToArray(),
            connection.Query<(long Id, string Label, long RateScaled)>(
                    "SELECT id,name,rate_pct FROM tax_rate WHERE is_active=1 ORDER BY rate_pct,id")
                .Select(static row => new RestoreOption(row.Id, row.Label, row.RateScaled)).ToArray());
    }

    /// <summary>The saved SKU when it is free, otherwise the fallback with the first free suffix.</summary>
    public static string SuggestSku(IDbConnection connection, IDbTransaction? transaction, string? saved, string fallback)
    {
        ArgumentNullException.ThrowIfNull(connection);
        bool Taken(string sku) => connection.ExecuteScalar<long>(
            "SELECT EXISTS(SELECT 1 FROM product WHERE sku=@sku COLLATE NOCASE)", new { sku }, transaction) != 0;
        if (!string.IsNullOrWhiteSpace(saved) && saved.Trim().Length <= 20 && !Taken(saved.Trim())) return saved.Trim();
        string suggested = fallback;
        for (int suffix = 1; suffix <= 255 && Taken(suggested); suffix++) suggested = $"{fallback}-{suffix:X}";
        return Taken(suggested) ? throw new DomainException(ErrorCode.SkuExists) : suggested;
    }

    /// <summary>Checks the choice against the draft inside the restoring transaction; a new product is created.</summary>
    public static RestoredProduct Resolve(DbTx transaction, RestoreDraft draft, RestoreProductChoice choice)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(choice);
        if (choice.ExistingProductId is null == (choice.NewProduct is null) || choice.SellingPrice < Money.Zero ||
            choice.Mrp < Money.Zero)
            throw new DomainException(ErrorCode.Validation, "Choose an existing product or a new one, and its prices.");
        if (choice.Mrp is { } mrp && choice.SellingPrice > mrp) throw new DomainException(ErrorCode.PriceAboveMrp);
        ProductRule product = choice.ExistingProductId is { } existing
            ? Existing(transaction, draft, existing)
            : Create(transaction, draft, choice);
        bool sameUnit = string.Equals(product.UnitCode, draft.UnitCode, StringComparison.OrdinalIgnoreCase);
        if (!sameUnit && !product.AllowFraction && !draft.Quantity.IsWhole)
            throw new DomainException(ErrorCode.FractionalQtyNotAllowed);
        DateOnly? expiry = draft.ExpiryDate ?? choice.ExpiryDate;
        if (product.TrackExpiry && expiry is null) throw new DomainException(ErrorCode.RestoreExpiryRequired);
        return new(product.Id, choice.SellingPrice, choice.Mrp, expiry, choice.NewProduct is not null);
    }

    private static ProductRule Existing(DbTx transaction, RestoreDraft draft, long productId)
    {
        ProductRule? product = transaction.Connection.QuerySingleOrDefault<ProductRule>("""
            SELECT p.id AS Id,p.is_active AS IsActive,p.product_class AS ProductClass,p.track_expiry AS TrackExpiry,
                   u.code AS UnitCode,u.allow_fraction AS AllowFraction
            FROM product p JOIN uom u ON u.id=p.uom_id WHERE p.id=@productId
            """, new { productId }, transaction.Transaction);
        if (product is not { IsActive: true }) throw new DomainException(ErrorCode.RestoreProductUnavailable);
        if (product.ProductClass != draft.ProductClass) throw new DomainException(ErrorCode.RestoreProductTypeMismatch);
        return product;
    }

    // The same rules as the product form: a free SKU, HSN of 4, 6 or 8 digits, and HSN with a
    // GST rate in Regular GST mode. The product takes the original's type and the lot's prices.
    private static ProductRule Create(DbTx transaction, RestoreDraft draft, RestoreProductChoice choice)
    {
        RestoreNewProduct input = choice.NewProduct!;
        string sku = Text(input.Sku, 20) ?? throw Invalid();
        string name = Text(input.Name, 120) ?? throw Invalid();
        string? local = Text(input.NameLocal, 120);
        string? hsn = Text(input.HsnCode, 8);
        if (hsn is not null && (hsn.Length is not (4 or 6 or 8) || hsn.Any(static character => !char.IsAsciiDigit(character))))
            throw Invalid();
        string? mode = transaction.Connection.ExecuteScalar<string?>(
            "SELECT value FROM setting WHERE key='tax.mode'", transaction: transaction.Transaction);
        if (mode == "REGULAR" && (hsn is null || input.TaxRateId is null)) throw Invalid();
        if (transaction.Connection.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM product WHERE sku=@sku COLLATE NOCASE)",
                new { sku }, transaction.Transaction) != 0)
            throw new DomainException(ErrorCode.SkuExists);
        if (transaction.Connection.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM category WHERE id=@id AND is_active=1)",
                new { id = input.CategoryId }, transaction.Transaction) == 0)
            throw new DomainException(ErrorCode.NotFound, "category", input.CategoryId);
        (string UnitCode, bool AllowFraction)? unit = transaction.Connection.QuerySingleOrDefault<(string, bool)?>(
            "SELECT code,allow_fraction FROM uom WHERE id=@id", new { id = input.UnitId }, transaction.Transaction);
        if (unit is not { } found) throw new DomainException(ErrorCode.NotFound, "unit", input.UnitId);
        LotTaxes.Rate(transaction, input.TaxRateId, activeOnly: true);
        long id = transaction.Connection.ExecuteScalar<long>("""
            INSERT INTO product(sku,name,name_local,category_id,uom_id,hsn_code,tax_rate_id,is_loose,track_expiry,
                default_selling_price,default_mrp,product_class)
            VALUES (@sku,@name,@local,@CategoryId,@UnitId,@hsn,@TaxRateId,@IsLoose,@TrackExpiry,@price,@mrp,@productClass)
            RETURNING id
            """, new
        {
            sku,
            name,
            local,
            input.CategoryId,
            input.UnitId,
            hsn,
            input.TaxRateId,
            input.IsLoose,
            input.TrackExpiry,
            price = choice.SellingPrice.Scaled,
            mrp = choice.Mrp?.Scaled,
            productClass = draft.ProductClass,
        }, transaction.Transaction);
        return new ProductRule
        {
            Id = id,
            IsActive = true,
            ProductClass = draft.ProductClass,
            TrackExpiry = input.TrackExpiry,
            UnitCode = found.UnitCode,
            AllowFraction = found.AllowFraction,
        };
    }

    private static string? Text(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        return trimmed.Length > maximum ? throw Invalid() : trimmed;
    }

    private static DomainException Invalid() =>
        new(ErrorCode.Validation, "Review the new product's SKU, name, HSN and GST rate.");

    private sealed class ProductRule
    {
        public long Id { get; init; }
        public bool IsActive { get; init; }
        public string ProductClass { get; init; } = "SALEABLE";
        public bool TrackExpiry { get; init; }
        public string UnitCode { get; init; } = string.Empty;
        public bool AllowFraction { get; init; }
    }
}
