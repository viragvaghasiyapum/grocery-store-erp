using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;

namespace GroceryErp.Sales;

public sealed partial class SalesService
{
    // Read-only preparation for Phase 5's return transaction. Null legacy fields
    // remain visible to the operator; no category, tax, or unit is guessed.
    public Task<ReturnRecreationDraft> GetReturnRecreationDraftAsync(long saleLineId,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        if (saleLineId <= 0) throw new DomainException(ErrorCode.Validation, "Invalid sale line.");
        RecreationRow? row = connection.QuerySingleOrDefault<RecreationRow>("""
            SELECT sl.id AS SaleLineId,sl.product_sku_snapshot AS Sku,
                   sl.product_name AS ProductName,sl.product_name_local AS ProductNameLocal,
                   sl.category_id_snapshot AS CategoryId,sl.category_name_snapshot AS CategoryName,
                   sl.uom_id_snapshot AS UnitId,sl.uom_snapshot AS UnitCode,
                   sl.tax_rate_id_snapshot AS TaxRateId,sl.tax_rate_pct AS TaxRateScaled,
                   sl.hsn_code AS HsnCode,sl.price_includes_tax AS PriceIncludesTax,
                   sl.track_expiry_snapshot AS TrackExpiry,sl.is_loose_snapshot AS IsLoose,
                   sl.product_class_snapshot AS ProductClass,sl.batch_no_snapshot AS BatchNumber,
                   sl.expiry_date_snapshot AS ExpiryDate,sl.unit_cost AS UnitCost,
                   sl.unit_price AS SellingPrice,sl.mrp AS Mrp,
                   sl.tax_mode_snapshot AS TaxMode
            FROM sale_line sl JOIN sale s ON s.id=sl.sale_id
            WHERE sl.id=@saleLineId AND s.status='COMPLETED'
            """, new { saleLineId });
        if (row is null) throw new DomainException(ErrorCode.NotFound, "completed sale line", saleLineId);
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(row.Sku)) missing.Add("SKU");
        if (string.IsNullOrWhiteSpace(row.ProductName)) missing.Add("product name");
        if (row.CategoryId is null || connection.ExecuteScalar<long>(
                "SELECT EXISTS(SELECT 1 FROM category WHERE id=@id)", new { id = row.CategoryId }) == 0)
            missing.Add("category");
        if (row.UnitId is null || connection.ExecuteScalar<long>(
                "SELECT EXISTS(SELECT 1 FROM uom WHERE id=@id)", new { id = row.UnitId }) == 0)
            missing.Add("unit");
        if (row.TaxRateId is null || connection.ExecuteScalar<long>(
                "SELECT EXISTS(SELECT 1 FROM tax_rate WHERE id=@id)", new { id = row.TaxRateId }) == 0)
            missing.Add("tax rate");
        if (string.IsNullOrWhiteSpace(row.TaxMode)) missing.Add("tax mode");
        if (row.TaxMode == "REGULAR" && string.IsNullOrWhiteSpace(row.HsnCode)) missing.Add("HSN");
        if (row.TrackExpiry is null) missing.Add("expiry tracking");
        if (row.IsLoose is null) missing.Add("quantity type");
        if (string.IsNullOrWhiteSpace(row.ProductClass)) missing.Add("classification");
        if (row.TrackExpiry == true && row.ExpiryDate is null) missing.Add("lot expiry");
        string baseSku = !string.IsNullOrWhiteSpace(row.Sku) && row.Sku.Length <= 20
            ? row.Sku : $"R{saleLineId:X}";
        string suggested = baseSku;
        if (connection.ExecuteScalar<long>(
                "SELECT EXISTS(SELECT 1 FROM product WHERE sku=@sku COLLATE NOCASE)",
                new { sku = suggested }) != 0)
        {
            baseSku = $"R{saleLineId:X}";
            suggested = baseSku;
            for (int suffix = 1; suffix <= 255 && connection.ExecuteScalar<long>(
                    "SELECT EXISTS(SELECT 1 FROM product WHERE sku=@sku COLLATE NOCASE)",
                    new { sku = suggested }) != 0; suffix++)
                suggested = $"{baseSku}-{suffix:X}";
            if (connection.ExecuteScalar<long>(
                    "SELECT EXISTS(SELECT 1 FROM product WHERE sku=@sku COLLATE NOCASE)",
                    new { sku = suggested }) != 0)
                throw new DomainException(ErrorCode.Validation, "No available return SKU was found.");
        }
        return new ReturnRecreationDraft(row.SaleLineId, suggested, row.ProductName, row.ProductNameLocal,
            row.CategoryId, row.CategoryName, row.UnitId, row.UnitCode,
            row.TaxRateId, Percent.FromScaled(row.TaxRateScaled), row.HsnCode,
            row.PriceIncludesTax, row.TrackExpiry, row.IsLoose, row.ProductClass,
            row.BatchNumber, row.ExpiryDate, Money.FromScaled(row.UnitCost),
            Money.FromScaled(row.SellingPrice), row.Mrp is null ? null : Money.FromScaled(row.Mrp.Value),
            missing);
    }, cancellationToken);

    private sealed class RecreationRow
    {
        public long SaleLineId { get; init; }
        public string? Sku { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public string? ProductNameLocal { get; init; }
        public long? CategoryId { get; init; }
        public string? CategoryName { get; init; }
        public long? UnitId { get; init; }
        public string? UnitCode { get; init; }
        public long? TaxRateId { get; init; }
        public long TaxRateScaled { get; init; }
        public string? HsnCode { get; init; }
        public bool PriceIncludesTax { get; init; }
        public bool? TrackExpiry { get; init; }
        public bool? IsLoose { get; init; }
        public string? ProductClass { get; init; }
        public string? BatchNumber { get; init; }
        public DateOnly? ExpiryDate { get; init; }
        public long UnitCost { get; init; }
        public long SellingPrice { get; init; }
        public long? Mrp { get; init; }
        public string? TaxMode { get; init; }
    }
}
