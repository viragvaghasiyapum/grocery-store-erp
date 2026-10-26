using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;

namespace GroceryErp.Inventory;

public sealed record DirectCountReview(long LotId, long ExpectedVersion, long OldQuantity,
    long CountedQuantity, DateOnly BusinessDate, string Reason, string OperationId,
    string DamageCategory = "OTHER");

public interface IDirectStockEngine
{
    long ReviewCount(DbTx tx, long lotId, long expectedVersion, long oldQuantity,
        long countedQuantity, DateOnly businessDate, string reason, string operationId,
        string damageCategory = "OTHER");
    IReadOnlyList<long> ReviewCountBatch(DbTx tx, IReadOnlyList<DirectCountReview> reviews);
    LotSourceMismatch? CheckLot(DbTx tx, long lotId);
    IEnumerable<LotSourceMismatch> CheckAll(DbTx tx, int pageSize = 1000);
}

// Reviewed physical counts and source reconciliation. Every other quantity change is
// written by its own document through LotQuantities.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance methods implement the stock engine contract.")]
public sealed class DirectStockEngine(DocNumberService numbers) : IDirectStockEngine
{
    // One reviewed physical count is one short transaction. A surplus starts a
    // distinct direct-origin lot; a deficit is a categorized damage document.
    public long ReviewCount(DbTx tx, long lotId, long expectedVersion, long oldQuantity,
        long countedQuantity, DateOnly businessDate, string reason, string operationId,
        string damageCategory = "OTHER")
    {
        if (countedQuantity < 0 || oldQuantity < 0 || string.IsNullOrWhiteSpace(reason) ||
            damageCategory is not ("EXPIRED" or "BROKEN" or "SPOILED" or "PEST" or "LOST" or "OTHER"))
            throw new DomainException(ErrorCode.Validation, "A count and reason are required.");
        OperationRef? prior = OperationGuard.Claim(tx, operationId, OpType.DirectCountReview);
        if (prior is not null)
        {
            if (prior.RefType != "DIRECT_COUNT")
                throw new DomainException(ErrorCode.Validation, "Operation ID belongs to another document.");
            CountResult replay = tx.Connection.QuerySingleOrDefault<CountResult>("""
                SELECT id AS Id,COALESCE(source_id,0) AS SourceId,source_kind AS SourceKind,
                       source_lot_id AS SourceLotId,old_quantity AS OldQuantity,
                       counted_quantity AS CountedQuantity,reason AS Reason
                FROM physical_count_result WHERE id=@id AND operation_id=@operationId
                """, new { id = prior.RefId, operationId }, tx.Transaction)
                ?? throw new DomainException(ErrorCode.Validation, "Count operation has no result.");
            ValidateCountReplay(tx, replay, lotId, oldQuantity, countedQuantity, reason, damageCategory);
            return replay.SourceId;
        }
        var existing = tx.Connection.QuerySingleOrDefault<CountResult>("""
            SELECT id AS Id,COALESCE(source_id,0) AS SourceId,source_kind AS SourceKind,
                   source_lot_id AS SourceLotId,old_quantity AS OldQuantity,
                   counted_quantity AS CountedQuantity,reason AS Reason
            FROM physical_count_result WHERE operation_id=@operationId
            """, new { operationId }, tx.Transaction);
        if (existing is not null)
        {
            ValidateCountReplay(tx, existing, lotId, oldQuantity, countedQuantity, reason, damageCategory);
            OperationGuard.Complete(tx, operationId, "DIRECT_COUNT", existing.Id);
            return existing.SourceId;
        }
        var lot = tx.Connection.QuerySingleOrDefault<CountLot>("""
            SELECT id AS LotId,product_id AS ProductId,qty_on_hand AS OnHand,
                   quantity_version AS Version,unit_cost AS Cost,selling_price AS Price,
                   mrp AS Mrp,expiry_date AS Expiry,batch_no AS Batch,
                   tax_rate_id_snapshot AS TaxRateId,tax_label_snapshot AS TaxLabel,
                   tax_rate_pct_snapshot AS TaxRatePct,unit_tax AS UnitTax,
                   unit_cost_incl_tax AS UnitCostInclTax
            FROM product_lot WHERE id=@lotId
            """, new { lotId }, tx.Transaction) ?? throw new DomainException(ErrorCode.NotFound, lotId);
        if (lot.Version != expectedVersion || lot.OnHand != oldQuantity)
            throw new DomainException(ErrorCode.StockCountReviewChanged, lotId);
        if (countedQuantity % 1000 != 0 && tx.Connection.ExecuteScalar<long>("""
            SELECT u.allow_fraction FROM product_lot l JOIN product p ON p.id=l.product_id
            JOIN uom u ON u.id=p.uom_id WHERE l.id=@lotId
            """, new { lotId }, tx.Transaction) == 0)
            throw new DomainException(ErrorCode.FractionalQtyNotAllowed, lotId);
        long resultId = tx.Connection.ExecuteScalar<long>("""
            INSERT INTO physical_count_result(operation_id,source_lot_id,old_quantity,
              counted_quantity,source_kind,reason)
            VALUES (@operationId,@lotId,@oldQuantity,@countedQuantity,'MATCH',@reason)
            RETURNING id
            """, new { operationId, lotId, oldQuantity, countedQuantity, reason }, tx.Transaction);
        string kind = "MATCH";
        long? sourceId = null;
        long delta = countedQuantity - oldQuantity;
        string date = businessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (delta < 0)
        {
            string document = numbers.Next(tx, DocType.Damage, businessDate);
            sourceId = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO damage(doc_no,damage_date,product_id,lot_id,qty,unit_cost,cost_value,reason,notes,
                    product_name_snapshot,product_sku_snapshot,product_name_local_snapshot,uom_snapshot,
                    product_class_snapshot,lot_code_snapshot,batch_no_snapshot,expiry_date_snapshot,
                    snapshot_provenance)
                SELECT @document,@date,p.id,l.id,@quantity,l.unit_cost,
                       CAST(ROUND(@quantity*l.unit_cost/1000.0) AS INTEGER),@damageCategory,@reason,
                       p.name,p.sku,p.name_local,u.code,p.product_class,l.lot_code,l.batch_no,
                       l.expiry_date,'AT_POST'
                FROM product_lot l JOIN product p ON p.id=l.product_id JOIN uom u ON u.id=p.uom_id
                WHERE l.id=@lotId
                RETURNING id
                """, new { document, date, lotId, quantity = -delta, reason, damageCategory }, tx.Transaction);
            Change(tx, lotId, delta, expectedVersion);
            CheckAffected(tx, [lotId]);
            kind = "DAMAGE";
        }
        else if (delta > 0)
        {
            // Found stock is the counted lot's stock: same cost and the same saved tax. A lot saved
            // before tax snapshots gets the product's rate as it is now.
            LotTax tax = lot.UnitTax is { } unitTax && lot.UnitCostInclTax is { } inclTax
                ? new LotTax(new TaxRateSnapshot(lot.TaxRateId, lot.TaxLabel, Percent.FromScaled(lot.TaxRatePct ?? 0)),
                    new PurchaseUnitCost(Money.FromScaled(inclTax - unitTax), Money.FromScaled(unitTax),
                        Money.FromScaled(inclTax), Money.FromScaled(lot.Cost)))
                : LotTaxes.FromBooked(tx, LotTaxes.ProductRate(tx, lot.ProductId), Money.FromScaled(lot.Cost));
            sourceId = tx.Connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,
                  unit_cost,mrp,selling_price,qty_received,direct_reason,
                  tax_rate_id_snapshot,tax_label_snapshot,tax_rate_pct_snapshot,unit_tax,unit_cost_incl_tax)
                VALUES (@productId,@code,@batch,@date,@expiry,@cost,@mrp,@price,@delta,@reason,
                  @taxRateId,@taxLabel,@taxRatePct,@unitTax,@unitCostInclTax)
                RETURNING id
                """, tax.AddTo(new DynamicParameters(new
            {
                productId = lot.ProductId,
                code = numbers.Next(tx, DocType.Lot, businessDate),
                batch = lot.Batch,
                date,
                expiry = lot.Expiry,
                cost = lot.Cost,
                mrp = lot.Mrp,
                price = lot.Price,
                delta,
                reason
            })), tx.Transaction);
            Change(tx, sourceId.Value, delta, 1);
            CheckAffected(tx, [lotId, sourceId.Value]);
            kind = "DIRECT_ORIGIN";
        }
        else CheckAffected(tx, [lotId]);
        tx.Connection.Execute("""
            UPDATE physical_count_result SET source_kind=@kind,source_id=@sourceId WHERE id=@resultId
            """, new { kind, sourceId, resultId }, tx.Transaction);
        OperationGuard.Complete(tx, operationId, "DIRECT_COUNT", resultId);
        return sourceId ?? 0;
    }

    public IReadOnlyList<long> ReviewCountBatch(DbTx tx, IReadOnlyList<DirectCountReview> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        if (reviews.Count is < 1 or > 200 || reviews.Select(static review => review.LotId).Distinct().Count() != reviews.Count)
            throw new DomainException(ErrorCode.Validation, "Select 1 to 200 distinct lots per count batch.");
        var sources = new List<long>(reviews.Count);
        foreach (DirectCountReview review in reviews)
            sources.Add(ReviewCount(tx, review.LotId, review.ExpectedVersion, review.OldQuantity,
                review.CountedQuantity, review.BusinessDate, review.Reason, review.OperationId,
                review.DamageCategory));
        return sources;
    }

    public LotSourceMismatch? CheckLot(DbTx tx, long lotId)
    {
        if (tx.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_lot WHERE id=@lotId",
                new { lotId }, tx.Transaction) == 0)
            throw new DomainException(ErrorCode.NotFound, lotId);
        return LotSourceReconciliation.CheckLot(tx.Connection, tx.Transaction, lotId);
    }

    public IEnumerable<LotSourceMismatch> CheckAll(DbTx tx, int pageSize = 1000) =>
        LotSourceReconciliation.CheckAll(tx.Connection, tx.Transaction, pageSize);

    private static void Change(DbTx tx, long lotId, long delta, long version)
    {
        int updated = tx.Connection.Execute("""
            UPDATE product_lot SET qty_on_hand=qty_on_hand+@delta
            WHERE id=@lotId AND qty_on_hand+@delta>=0 AND quantity_version=@version
            """, new { lotId, delta, version }, tx.Transaction);
        if (updated != 1) throw new DomainException(ErrorCode.StockCountReviewChanged, lotId);
    }

    private void CheckAffected(DbTx tx, IEnumerable<long> lotIds)
    {
        foreach (long lotId in lotIds.Distinct())
            if (CheckLot(tx, lotId) is { } mismatch)
                throw new DomainException(ErrorCode.Validation,
                    $"Lot {lotId} source balance {mismatch.Expected} differs from {mismatch.Actual}.");
    }

    private static void ValidateCountReplay(DbTx tx, CountResult result, long lotId, long oldQuantity,
        long countedQuantity, string reason, string damageCategory)
    {
        if (result.SourceLotId != lotId || result.OldQuantity != oldQuantity ||
            result.CountedQuantity != countedQuantity || result.Reason != reason)
            throw new DomainException(ErrorCode.Validation, "Operation ID belongs to another count.");
        if (result.SourceKind == "DAMAGE" && tx.Connection.ExecuteScalar<string>(
                "SELECT reason FROM damage WHERE id=@id", new { id = result.SourceId }, tx.Transaction)
            != damageCategory)
            throw new DomainException(ErrorCode.Validation, "Operation ID belongs to another damage category.");
    }

    private sealed class CountLot
    {
        public long LotId { get; init; }
        public long ProductId { get; init; }
        public long OnHand { get; init; }
        public long Version { get; init; }
        public long Cost { get; init; }
        public long Price { get; init; }
        public long? Mrp { get; init; }
        public string? Expiry { get; init; }
        public string? Batch { get; init; }
        public long? TaxRateId { get; init; }
        public string? TaxLabel { get; init; }
        public long? TaxRatePct { get; init; }
        public long? UnitTax { get; init; }
        public long? UnitCostInclTax { get; init; }
    }
    private sealed class CountResult
    {
        public long Id { get; init; }
        public long SourceId { get; init; }
        public string SourceKind { get; init; } = "";
        public long SourceLotId { get; init; }
        public long OldQuantity { get; init; }
        public long CountedQuantity { get; init; }
        public string Reason { get; init; } = "";
    }
}
