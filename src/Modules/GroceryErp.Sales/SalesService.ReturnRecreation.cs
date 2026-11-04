using System.Data;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory;

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

    // A resellable return whose sold lot changed or was deleted starts a new lot from the sale
    // snapshot. When the product was deleted, the operator's choice decides its product: an
    // existing one or a new one. Without a choice, an earlier return's active product for the
    // same original is reused, or a product is recreated once from complete saved fields.
    private (long ProductId, long LotId) CreateReturnLot(DbTx tx, long saleLineId,
        long returnLineId, Quantity quantity, DateOnly returnDate, RestoreProductChoice? restore)
    {
        RecreationRow row = tx.Connection.QuerySingle<RecreationRow>(RecreationSql,
            new { saleLineId }, tx.Transaction);
        // Every price includes tax. A line saved as excluding tax charged its rate on top, so the
        // returned stock's price is that customer price, capped at MRP.
        Money price = SavedPrice(row);
        long? mrp = row.Mrp;
        DateOnly? expiry = row.ExpiryDate;
        long productId = row.ProductId;
        RestoredProduct? chosen = null;
        bool productExists = tx.Connection.ExecuteScalar<long>(
            "SELECT EXISTS(SELECT 1 FROM product WHERE id=@productId)", new { productId }, tx.Transaction) == 1;
        long? earlier = productExists ? null : EarlierRestock(tx.Connection, tx.Transaction, productId);
        if (earlier is { } existing) productId = existing;
        else if (!productExists && restore is not null)
        {
            chosen = RestoreProducts.Resolve(tx, Draft(tx.Connection, tx.Transaction, row, quantity), restore);
            (productId, price, mrp, expiry) = (chosen.ProductId, chosen.SellingPrice, chosen.Mrp?.Scaled, chosen.ExpiryDate);
        }
        else if (!productExists) productId = RecreateProduct(tx, row, price);
        if (expiry is { } date && date < returnDate) throw new DomainException(ErrorCode.ExpiredLot, date);
        // The lot keeps the cost-side tax the sale line copied from the sold lot. A line saved
        // before tax snapshots derives it from its booked cost: at the sale line's rate, or at the
        // chosen product's rate when the operator chose one.
        var rate = new TaxRateSnapshot(row.TaxRateId, row.TaxLabel, Percent.FromScaled(row.TaxRateScaled));
        LotTax tax = row.UnitCostTax is { } unitTax && row.UnitCostInclTax is { } inclTax
            ? new LotTax(rate, new PurchaseUnitCost(Money.FromScaled(inclTax - unitTax), Money.FromScaled(unitTax),
                Money.FromScaled(inclTax), Money.FromScaled(row.UnitCost)))
            : LotTaxes.FromBooked(tx, chosen is null ? rate : LotTaxes.ProductRate(tx, productId),
                Money.FromScaled(row.UnitCost));
        string lotCode = documentNumbers.Next(tx, DocType.Lot, returnDate);
        long lotId = tx.Connection.ExecuteScalar<long>("""
            INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,
                unit_cost,mrp,selling_price,qty_received,origin_kind,origin_ref_id,
                tax_rate_id_snapshot,tax_label_snapshot,tax_rate_pct_snapshot,unit_tax,unit_cost_incl_tax)
            VALUES (@productId,@lotCode,@BatchNumber,@receivedDate,@expiry,
                @UnitCost,@mrp,@price,@quantity,'CUSTOMER_RETURN',@returnLineId,
                @taxRateId,@taxLabel,@taxRatePct,@unitTax,@unitCostInclTax)
            RETURNING id
            """, tax.AddTo(new DynamicParameters(new
        {
            productId,
            lotCode,
            row.BatchNumber,
            receivedDate = returnDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            expiry,
            row.UnitCost,
            mrp,
            price = price.Scaled,
            quantity = quantity.Scaled,
            returnLineId
        })), tx.Transaction);
        if (chosen is not null)
            auditLog.Write(tx, "sale_return_line", returnLineId, "RESTORE_PRODUCT", new
            {
                saleLineId,
                originalProductId = row.ProductId,
                productId,
                createdProduct = chosen.Created,
                lotId,
            });
        return (productId, lotId);
    }

    // A deleted product is recreated once under a new ID and a conflict-safe SKU, only from
    // complete saved fields; the return form asks the operator instead.
    private static long RecreateProduct(DbTx tx, RecreationRow row, Money price)
    {
        if (string.IsNullOrWhiteSpace(row.ProductName) || row.CategoryId is null ||
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
                "The deleted product has incomplete historical fields. Choose a product for the returned units.");
        string sku = RestoreProducts.SuggestSku(tx.Connection, tx.Transaction, row.Sku, $"R{row.SaleLineId:X}");
        return tx.Connection.ExecuteScalar<long>("""
            INSERT INTO product(sku,name,name_local,category_id,uom_id,hsn_code,
                tax_rate_id,is_loose,track_expiry,
                default_selling_price,default_mrp,product_class)
            VALUES (@sku,@ProductName,@ProductNameLocal,@CategoryId,@UnitId,@HsnCode,
                @TaxRateId,@IsLoose,@TrackExpiry,
                @price,@Mrp,'SALEABLE') RETURNING id
            """, new
        {
            sku,
            row.ProductName,
            row.ProductNameLocal,
            row.CategoryId,
            row.UnitId,
            row.HsnCode,
            row.TaxRateId,
            row.IsLoose,
            row.TrackExpiry,
            price = price.Scaled,
            row.Mrp
        }, tx.Transaction);
    }

    // The active product an earlier return of the same deleted original went into.
    private static long? EarlierRestock(IDbConnection connection, IDbTransaction? transaction, long productId) =>
        connection.ExecuteScalar<long?>("""
            SELECT rl.restock_product_id_snapshot FROM sale_return_line rl
            WHERE rl.product_id_snapshot=@productId
              AND rl.restock_product_id_snapshot<>rl.product_id_snapshot
              AND EXISTS(SELECT 1 FROM product p WHERE p.id=rl.restock_product_id_snapshot AND p.is_active=1)
            ORDER BY rl.id DESC LIMIT 1
            """, new { productId }, transaction);

    private static Money SavedPrice(RecreationRow row)
    {
        Money price = Money.FromScaled(row.SellingPrice);
        if (!row.PriceIncludesTax) price += price.PercentOf(Percent.FromScaled(row.TaxRateScaled));
        return row.Mrp is { } mrp && price.Scaled > mrp ? Money.FromScaled(mrp) : price;
    }

    // A saved category, unit or rate that no longer exists counts as not saved.
    private static RestoreDraft Draft(IDbConnection connection, IDbTransaction? transaction, RecreationRow row,
        Quantity quantity)
    {
        bool Exists(string table, long? id) => id is not null && connection.ExecuteScalar<long>(
            $"SELECT EXISTS(SELECT 1 FROM {table} WHERE id=@id)", new { id }, transaction) != 0;
        return new RestoreDraft(row.SaleLineId, row.InvoiceNumber ?? "", row.ProductName, row.ProductNameLocal,
            RestoreProducts.SuggestSku(connection, transaction, row.Sku, $"R{row.SaleLineId:X}"),
            Exists("category", row.CategoryId) ? row.CategoryId : null,
            Exists("uom", row.UnitId) ? row.UnitId : null, row.UnitCode,
            Exists("tax_rate", row.TaxRateId) ? row.TaxRateId : null, row.HsnCode,
            row.TrackExpiry, row.IsLoose, string.IsNullOrWhiteSpace(row.ProductClass) ? "SALEABLE" : row.ProductClass,
            row.BatchNumber, row.ExpiryDate, quantity, Money.FromScaled(row.UnitCost), SavedPrice(row),
            row.Mrp is { } mrp ? Money.FromScaled(mrp) : null);
    }

    /// <summary>
    /// The saved details for a resold return whose product was deleted, or null when the units have
    /// a product: it still exists, or an earlier return of the same original went into an active one.
    /// </summary>
    public Task<RestoreDraft?> GetReturnRestoreDraftAsync(long saleLineId, Quantity quantity,
        CancellationToken cancellationToken = default) => database.ReadAsync(connection =>
    {
        if (saleLineId <= 0) throw new DomainException(ErrorCode.Validation, "Invalid sale line.");
        RecreationRow? row = connection.QuerySingleOrDefault<RecreationRow>(RecreationSql + """
             AND EXISTS(SELECT 1 FROM sale s WHERE s.id=sl.sale_id AND s.status='COMPLETED')
            """, new { saleLineId });
        if (row is null) throw new DomainException(ErrorCode.NotFound, "completed sale line", saleLineId);
        bool productExists = connection.ExecuteScalar<long>(
            "SELECT EXISTS(SELECT 1 FROM product WHERE id=@id)", new { id = row.ProductId }) == 1;
        return productExists || EarlierRestock(connection, null, row.ProductId) is not null
            ? null
            : Draft(connection, null, row, quantity);
    }, cancellationToken);

    private const string RecreationSql = """
        SELECT sl.id AS SaleLineId,sl.product_id AS ProductId,
               (SELECT invoice_no FROM sale WHERE id=sl.sale_id) AS InvoiceNumber,
               sl.product_sku_snapshot AS Sku,sl.product_name AS ProductName,
               sl.product_name_local AS ProductNameLocal,
               sl.category_id_snapshot AS CategoryId,
               sl.uom_id_snapshot AS UnitId,sl.uom_snapshot AS UnitCode,sl.tax_rate_id_snapshot AS TaxRateId,
               sl.tax_rate_pct AS TaxRateScaled,sl.hsn_code AS HsnCode,
               sl.price_includes_tax AS PriceIncludesTax,
               sl.track_expiry_snapshot AS TrackExpiry,sl.is_loose_snapshot AS IsLoose,
               sl.product_class_snapshot AS ProductClass,
               sl.batch_no_snapshot AS BatchNumber,
               sl.expiry_date_snapshot AS ExpiryDate,sl.unit_cost AS UnitCost,
               sl.unit_price AS SellingPrice,sl.mrp AS Mrp,
               sl.tax_mode_snapshot AS TaxMode,sl.tax_label_snapshot AS TaxLabel,
               sl.unit_cost_tax AS UnitCostTax,sl.unit_cost_incl_tax AS UnitCostInclTax
        FROM sale_line sl WHERE sl.id=@saleLineId
        """;

    private sealed record RecreationRow
    {
        public long SaleLineId { get; init; }
        public long ProductId { get; init; }
        public string? InvoiceNumber { get; init; }
        public string? Sku { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public string? ProductNameLocal { get; init; }
        public long? CategoryId { get; init; }
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
        public string? TaxLabel { get; init; }
        public long? UnitCostTax { get; init; }
        public long? UnitCostInclTax { get; init; }
    }
}
