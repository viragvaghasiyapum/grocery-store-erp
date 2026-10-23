using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;

namespace GroceryErp.Sales;

public sealed partial class SalesService
{
    private static Dictionary<long, long> AllocateRefundableTotals(
        IReadOnlyList<(long Id, long Total)> lines, long roundOff)
    {
        Money[] weights = lines.Select(static line => Money.FromScaled(line.Total)).ToArray();
        IReadOnlyList<Money> shares = MoneyMath.Allocate(Money.FromScaled(Math.Abs(roundOff)), weights);
        var totals = new Dictionary<long, long>(lines.Count);
        for (int index = 0; index < lines.Count; index++)
            totals.Add(lines[index].Id, checked(lines[index].Total +
                (roundOff < 0 ? -shares[index].Scaled : shares[index].Scaled)));
        return totals;
    }

    private sealed record PendingReturnDamage(long ReturnLineId, long SaleLineId,
        long ProductId, long LotId, long Quantity, long UnitCost, long Cost);

    private long CreateReturnDamage(DbTx tx, DateOnly date, string reason,
        PendingReturnDamage pending)
    {
        string number = documentNumbers.Next(tx, DocType.Damage, date);
        long damageId = tx.Connection.ExecuteScalar<long>("""
            INSERT INTO damage(doc_no,damage_date,product_id,lot_id,qty,unit_cost,cost_value,
                reason,notes,product_name_snapshot,product_sku_snapshot,
                product_name_local_snapshot,uom_snapshot,product_class_snapshot,
                lot_code_snapshot,batch_no_snapshot,expiry_date_snapshot,snapshot_provenance,
                legacy_unknown_fields)
            SELECT @number,@date,@ProductId,@LotId,@Quantity,@UnitCost,@Cost,
                'CUSTOMER_RETURN',@reason,sl.product_name,sl.product_sku_snapshot,
                sl.product_name_local,sl.uom_snapshot,sl.product_class_snapshot,
                sl.lot_code_snapshot,sl.batch_no_snapshot,sl.expiry_date_snapshot,
                'SALE_LINE_COPY',sl.legacy_unknown_fields
            FROM sale_line sl WHERE sl.id=@SaleLineId RETURNING id
            """, new
        {
            number,
            date = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            pending.ProductId,
            pending.LotId,
            pending.Quantity,
            pending.UnitCost,
            pending.Cost,
            reason,
            pending.SaleLineId
        }, tx.Transaction);
        tx.Connection.Execute("UPDATE sale_return_line SET damage_id=@damageId WHERE id=@returnLineId",
            new { damageId, returnLineId = pending.ReturnLineId }, tx.Transaction);
        return damageId;
    }

    // A resellable return whose sold lot changed or was deleted starts a new lot from
    // the sale snapshot. A deleted product is recreated once under a new ID and a
    // conflict-safe SKU; later returns of the same original reuse that product.
    private (long ProductId, long LotId) CreateReturnLot(DbTx tx, long saleLineId,
        long returnLineId, Quantity quantity, DateOnly returnDate, ReturnRecreationInput? reviewed)
    {
        RecreationRow row = tx.Connection.QuerySingle<RecreationRow>("""
            SELECT sl.id AS SaleLineId,sl.product_id AS ProductId,
                   sl.product_sku_snapshot AS Sku,sl.product_name AS ProductName,
                   sl.product_name_local AS ProductNameLocal,
                   sl.category_id_snapshot AS CategoryId,
                   sl.uom_id_snapshot AS UnitId,sl.tax_rate_id_snapshot AS TaxRateId,
                   sl.tax_rate_pct AS TaxRateScaled,sl.hsn_code AS HsnCode,
                   sl.price_includes_tax AS PriceIncludesTax,
                   sl.track_expiry_snapshot AS TrackExpiry,sl.is_loose_snapshot AS IsLoose,
                   sl.product_class_snapshot AS ProductClass,
                   sl.batch_no_snapshot AS BatchNumber,
                   sl.expiry_date_snapshot AS ExpiryDate,sl.unit_cost AS UnitCost,
                   sl.unit_price AS SellingPrice,sl.mrp AS Mrp,
                   sl.tax_mode_snapshot AS TaxMode
            FROM sale_line sl WHERE sl.id=@saleLineId
            """, new { saleLineId }, tx.Transaction);
        if (reviewed is not null)
        {
            if (string.IsNullOrWhiteSpace(reviewed.Sku) || reviewed.Sku.Length > 20 ||
                string.IsNullOrWhiteSpace(reviewed.ProductName) || reviewed.CategoryId <= 0 ||
                reviewed.UnitId <= 0 || reviewed.TaxRateId <= 0 ||
                reviewed.ProductClass != "SALEABLE" ||
                reviewed.TaxMode is not ("REGULAR" or "COMPOSITION" or "NONE") ||
                reviewed.TaxMode == "REGULAR" && string.IsNullOrWhiteSpace(reviewed.HsnCode) ||
                reviewed.TrackExpiry && reviewed.ExpiryDate is null ||
                tx.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM tax_rate WHERE id=@id",
                    new { id = reviewed.TaxRateId }, tx.Transaction) != 1)
                throw new DomainException(ErrorCode.Validation, "Review all return recreation fields.");
            row = row with
            {
                Sku = reviewed.Sku.Trim(),
                ProductName = reviewed.ProductName.Trim(),
                ProductNameLocal = reviewed.ProductNameLocal?.Trim(),
                CategoryId = reviewed.CategoryId,
                UnitId = reviewed.UnitId,
                TaxRateId = reviewed.TaxRateId,
                TaxMode = reviewed.TaxMode,
                HsnCode = reviewed.HsnCode?.Trim(),
                PriceIncludesTax = reviewed.PriceIncludesTax,
                TrackExpiry = reviewed.TrackExpiry,
                IsLoose = reviewed.IsLoose,
                ProductClass = reviewed.ProductClass,
                ExpiryDate = reviewed.ExpiryDate,
            };
            tx.Connection.Execute("""
                UPDATE sale_return_line SET product_sku_snapshot=@Sku,
                    product_name_snapshot=@ProductName,product_name_local_snapshot=@ProductNameLocal,
                    category_id_snapshot=@CategoryId,uom_id_snapshot=@UnitId,
                    tax_rate_id_snapshot=@TaxRateId,tax_mode_snapshot=@TaxMode,
                    price_includes_tax_snapshot=@PriceIncludesTax,
                    track_expiry_snapshot=@TrackExpiry,is_loose_snapshot=@IsLoose,
                    expiry_date_snapshot=@ExpiryDate,snapshot_provenance='OPERATOR_REVIEW',
                    legacy_unknown_fields=NULL WHERE id=@returnLineId
                """, new
            {
                row.Sku,
                row.ProductName,
                row.ProductNameLocal,
                row.CategoryId,
                row.UnitId,
                row.TaxRateId,
                row.TaxMode,
                row.PriceIncludesTax,
                row.TrackExpiry,
                row.IsLoose,
                row.ExpiryDate,
                returnLineId
            }, tx.Transaction);
        }
        if (row.ExpiryDate is { } expiry && expiry < returnDate)
            throw new DomainException(ErrorCode.ExpiredLot, expiry);
        long productId = row.ProductId;
        bool productExists = tx.Connection.ExecuteScalar<long>(
            "SELECT EXISTS(SELECT 1 FROM product WHERE id=@productId)", new { productId }, tx.Transaction) == 1;
        long? recreated = productExists ? null : tx.Connection.ExecuteScalar<long?>("""
            SELECT rl.restock_product_id_snapshot FROM sale_return_line rl
            WHERE rl.product_id_snapshot=@productId
              AND rl.restock_product_id_snapshot<>rl.product_id_snapshot
              AND EXISTS(SELECT 1 FROM product p WHERE p.id=rl.restock_product_id_snapshot)
            ORDER BY rl.id DESC LIMIT 1
            """, new { productId }, tx.Transaction);
        if (recreated is { } existing) productId = existing;
        else if (!productExists)
        {
            if (string.IsNullOrWhiteSpace(row.Sku) || string.IsNullOrWhiteSpace(row.ProductName) || row.CategoryId is null ||
                row.UnitId is null || row.TaxRateId is null || row.TrackExpiry is null ||
                row.IsLoose is null || row.ProductClass != "SALEABLE" ||
                string.IsNullOrWhiteSpace(row.TaxMode) ||
                row.TaxMode == "REGULAR" && string.IsNullOrWhiteSpace(row.HsnCode) ||
                row.TrackExpiry == true && row.ExpiryDate is null ||
                tx.Connection.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM category WHERE id=@id)",
                    new { id = row.CategoryId }, tx.Transaction) == 0 ||
                tx.Connection.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM uom WHERE id=@id)",
                    new { id = row.UnitId }, tx.Transaction) == 0 ||
                tx.Connection.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM tax_rate WHERE id=@id)",
                    new { id = row.TaxRateId }, tx.Transaction) == 0)
                throw new DomainException(ErrorCode.Validation,
                    "The deleted product has incomplete historical fields. Review its return recreation details.");
            string sku = !string.IsNullOrWhiteSpace(row.Sku) && row.Sku.Length <= 20 &&
                tx.Connection.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM product WHERE sku=@sku COLLATE NOCASE)",
                    new { sku = row.Sku }, tx.Transaction) == 0
                ? row.Sku : $"R{saleLineId:X}";
            for (int suffix = 1; suffix <= 255 && tx.Connection.ExecuteScalar<long>(
                    "SELECT EXISTS(SELECT 1 FROM product WHERE sku=@sku COLLATE NOCASE)",
                    new { sku }, tx.Transaction) != 0; suffix++)
                sku = $"R{saleLineId:X}-{suffix:X}";
            if (tx.Connection.ExecuteScalar<long>(
                    "SELECT EXISTS(SELECT 1 FROM product WHERE sku=@sku COLLATE NOCASE)",
                    new { sku }, tx.Transaction) != 0)
                throw new DomainException(ErrorCode.Validation, "No return SKU is available for the deleted product.");
            productId = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product(sku,name,name_local,category_id,uom_id,hsn_code,
                    tax_rate_id,price_includes_tax,is_loose,track_expiry,
                    default_selling_price,default_mrp,product_class)
                VALUES (@sku,@ProductName,@ProductNameLocal,@CategoryId,@UnitId,@HsnCode,
                    @TaxRateId,@PriceIncludesTax,@IsLoose,@TrackExpiry,
                    @SellingPrice,@Mrp,'SALEABLE') RETURNING id
                """, new
            {
                sku,
                row.ProductName,
                row.ProductNameLocal,
                row.CategoryId,
                row.UnitId,
                row.HsnCode,
                row.TaxRateId,
                row.PriceIncludesTax,
                row.IsLoose,
                row.TrackExpiry,
                row.SellingPrice,
                row.Mrp
            }, tx.Transaction);
        }
        string lotCode = documentNumbers.Next(tx, DocType.Lot, returnDate);
        long lotId = tx.Connection.ExecuteScalar<long>("""
            INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,
                unit_cost,mrp,selling_price,qty_received,origin_kind,origin_ref_id)
            VALUES (@productId,@lotCode,@BatchNumber,@receivedDate,@ExpiryDate,
                @UnitCost,@Mrp,@SellingPrice,@quantity,'CUSTOMER_RETURN',@returnLineId)
            RETURNING id
            """, new
        {
            productId,
            lotCode,
            row.BatchNumber,
            receivedDate = returnDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            row.ExpiryDate,
            row.UnitCost,
            row.Mrp,
            row.SellingPrice,
            quantity = quantity.Scaled,
            returnLineId
        }, tx.Transaction);
        return (productId, lotId);
    }

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
            missing)
        {
            TaxMode = row.TaxMode,
            // A product an earlier return already recreated is reused without review.
            ProductMissing = connection.ExecuteScalar<long>("""
                SELECT NOT EXISTS(SELECT 1 FROM product WHERE id=sl.product_id)
                   AND NOT EXISTS(SELECT 1 FROM sale_return_line rl
                       WHERE rl.product_id_snapshot=sl.product_id
                         AND rl.restock_product_id_snapshot<>rl.product_id_snapshot
                         AND EXISTS(SELECT 1 FROM product p WHERE p.id=rl.restock_product_id_snapshot))
                FROM sale_line sl WHERE sl.id=@id
                """, new { id = row.SaleLineId }) == 1,
        };
    }, cancellationToken);

    public Task<ReturnRecreationOptions> GetReturnRecreationOptionsAsync(
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
            new ReturnRecreationOptions(
                connection.Query<ReturnRecreationOption>(
                    "SELECT id AS Id,name AS Label FROM category ORDER BY name,id").ToArray(),
                connection.Query<ReturnRecreationOption>(
                    "SELECT id AS Id,code||' — '||name AS Label FROM uom ORDER BY code,id").ToArray(),
                connection.Query<ReturnRecreationOption>(
                    "SELECT id AS Id,name AS Label,rate_pct AS RateScaled FROM tax_rate ORDER BY rate_pct,id").ToArray()),
            cancellationToken);

    private sealed record RecreationRow
    {
        public long SaleLineId { get; init; }
        public long ProductId { get; init; }
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
