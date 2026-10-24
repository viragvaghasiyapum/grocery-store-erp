using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;

namespace GroceryErp.Catalog;

public sealed partial class CatalogService
{
    /// <summary>
    /// Resolves each selected product's eligibility, stock and cost for a bulk action without
    /// changing anything. Every skipped row carries the localized reason it was skipped.
    /// </summary>
    public Task<CatalogBulkPreview> PreviewBulkAsync(IReadOnlyCollection<long> productIds,
        CatalogBulkCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        long[] ids = BulkBatch.ValidateIds(productIds, MaxBulkProducts);
        ValidateCommand(command);
        return database.ReadAsync(connection =>
        {
            Dictionary<long, BulkProductRow> rows = [];
            foreach (long[] chunk in ids.Chunk(500))
                foreach (BulkProductRow row in connection.Query<BulkProductRow>(BulkRowsSql, new { ids = chunk }))
                    rows[row.Id] = row;
            var preview = new List<CatalogBulkRow>(ids.Length);
            foreach (long id in ids)
            {
                if (!rows.TryGetValue(id, out BulkProductRow? row))
                {
                    preview.Add(new CatalogBulkRow(id, string.Empty, string.Empty, false, "Bulk_Missing",
                        Quantity.Zero, Money.Zero));
                    continue;
                }
                string? reason = SkipReason(row, command);
                preview.Add(new CatalogBulkRow(id, row.Sku, row.Name, reason is null, reason,
                    Quantity.FromScaled(row.Quantity), Money.FromScaled(row.CostValue)));
            }
            CatalogBulkRow[] eligible = preview.Where(static row => row.Eligible).ToArray();
            return new CatalogBulkPreview(ids.Length, eligible.Select(static row => row.Id).ToArray(),
                ids.Length - eligible.Length,
                Quantity.FromScaled(eligible.Sum(static row => row.Quantity.Scaled)),
                Money.FromScaled(eligible.Sum(static row => row.CostValue.Scaled)))
            { Rows = preview };
        }, cancellationToken);
    }

    /// <summary>
    /// Applies one bulk batch of at most <see cref="BulkBatch.MaxBatchSize"/> products in one
    /// transaction. Eligibility is rechecked inside the transaction; rows that changed since the
    /// review are reported, not forced. A batch replayed with the same operation ID changes nothing.
    /// </summary>
    public Task<IReadOnlyList<BulkItemResult>> RunBulkBatchAsync(CatalogBulkCommand command,
        IReadOnlyCollection<long> productIds, string batchOperationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        long[] ids = BulkBatch.ValidateIds(productIds);
        ValidateCommand(command);
        if (command.Action == CatalogBulkAction.Labels)
            throw new DomainException(ErrorCode.Validation, "Labels are printed, not saved, in bulk.");
        return database.WriteAsync(transaction =>
        {
            if (BulkBatch.Claim(transaction, batchOperationId, ids) is { } replay) return replay;
            if (command.Action is CatalogBulkAction.Category or CatalogBulkAction.Tax)
            {
                string table = command.Action == CatalogBulkAction.Category ? "category" : "tax_rate";
                if (transaction.Connection.ExecuteScalar<long>(
                        $"SELECT COUNT(*) FROM {table} WHERE id=@id AND is_active=1",
                        new { id = command.TargetId }, transaction.Transaction) == 0)
                    throw new DomainException(ErrorCode.NotFound, table, command.TargetId ?? 0);
            }
            Dictionary<long, BulkProductRow> rows = transaction.Connection
                .Query<BulkProductRow>(BulkRowsSql, new { ids }, transaction.Transaction)
                .ToDictionary(static row => row.Id);
            var results = new List<BulkItemResult>(ids.Length);
            foreach (long id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!rows.TryGetValue(id, out BulkProductRow? row))
                {
                    results.Add(new BulkItemResult(id, BulkItemOutcome.Missing, "Bulk_Missing"));
                    continue;
                }
                if (SkipReason(row, command) is { } reason)
                {
                    results.Add(new BulkItemResult(id, BulkItemOutcome.NotEligible, reason));
                    continue;
                }
                Apply(transaction, command, id);
                results.Add(new BulkItemResult(id, BulkItemOutcome.Changed));
            }
            long[] changed = results.Where(static result => result.Outcome == BulkItemOutcome.Changed)
                .Select(static result => result.Id).ToArray();
            audit.Write(transaction, "product", 0, "BULK_" + command.Action.ToString().ToUpperInvariant(),
                new { batchOperationId, command.TargetId, command.TargetCode, changed });
            return BulkBatch.Complete(transaction, batchOperationId, results);
        }, cancellationToken);
    }

    private void Apply(DbTx transaction, CatalogBulkCommand command, long id)
    {
        switch (command.Action)
        {
            case CatalogBulkAction.Archive:
                Remove(transaction, id);
                break;
            case CatalogBulkAction.Restore:
                transaction.Connection.Execute(
                    "UPDATE product SET is_active=1,updated_at=datetime('now','localtime') WHERE id=@id AND is_active=0",
                    new { id }, transaction.Transaction);
                audit.Write(transaction, "product", id, "RESTORE", null);
                break;
            case CatalogBulkAction.Category:
                transaction.Connection.Execute(
                    "UPDATE product SET category_id=@target,updated_at=datetime('now','localtime') WHERE id=@id",
                    new { id, target = command.TargetId }, transaction.Transaction);
                break;
            case CatalogBulkAction.Tax:
                transaction.Connection.Execute(
                    "UPDATE product SET tax_rate_id=@target,updated_at=datetime('now','localtime') WHERE id=@id",
                    new { id, target = command.TargetId }, transaction.Transaction);
                break;
            case CatalogBulkAction.Classification:
                transaction.Connection.Execute(
                    "UPDATE product SET product_class=@target,updated_at=datetime('now','localtime') WHERE id=@id",
                    new { id, target = command.TargetCode }, transaction.Transaction);
                break;
            case CatalogBulkAction.Barcode:
                EnsureInternalBarcode(transaction, id, command.TargetCode!);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static void ValidateCommand(CatalogBulkCommand command)
    {
        bool valid = command.Action switch
        {
            CatalogBulkAction.Category or CatalogBulkAction.Tax => command.TargetId > 0,
            CatalogBulkAction.Classification => command.TargetCode is "SALEABLE" or "INTERNAL",
            CatalogBulkAction.Barcode => !string.IsNullOrWhiteSpace(command.TargetCode),
            _ => true,
        };
        if (!valid) throw new DomainException(ErrorCode.Validation, "Choose the value to apply to the selected products.");
    }

    private static string? SkipReason(BulkProductRow row, CatalogBulkCommand command) => command.Action switch
    {
        CatalogBulkAction.Archive => row.IsActive ? null : "Bulk_Reason_AlreadyArchived",
        CatalogBulkAction.Restore => row.IsActive ? "Bulk_Reason_AlreadyActive" : null,
        CatalogBulkAction.Labels => row.IsActive ? null : "Bulk_Reason_Archived",
        CatalogBulkAction.Barcode => !row.IsActive ? "Bulk_Reason_Archived"
            : row.HasInternalBarcode ? "Bulk_Reason_HasInternalBarcode" : null,
        CatalogBulkAction.Category => row.CategoryId == command.TargetId ? "Bulk_Reason_NoChange" : null,
        CatalogBulkAction.Tax => row.TaxRateId == command.TargetId ? "Bulk_Reason_NoChange" : null,
        CatalogBulkAction.Classification => row.ProductClass == command.TargetCode ? "Bulk_Reason_NoChange"
            : row.HasStock ? "Bulk_Reason_HasStock" : null,
        _ => null,
    };

    private const string BulkRowsSql = """
        SELECT p.id AS Id,p.sku AS Sku,p.name AS Name,p.is_active AS IsActive,p.product_class AS ProductClass,
               p.category_id AS CategoryId,p.tax_rate_id AS TaxRateId,
               COALESCE(t.quantity,0) AS Quantity,COALESCE(t.cost_value,0) AS CostValue,
               EXISTS(SELECT 1 FROM product_lot l WHERE l.product_id=p.id AND l.qty_on_hand<>0) AS HasStock,
               EXISTS(SELECT 1 FROM product_barcode b WHERE b.product_id=p.id AND b.lot_id IS NULL
                      AND b.source='INTERNAL') AS HasInternalBarcode
        FROM product p LEFT JOIN product_stock_total t ON t.product_id=p.id
        WHERE p.id IN @ids
        """;

    private sealed class BulkProductRow
    {
        public long Id { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public string ProductClass { get; init; } = "SALEABLE";
        public long CategoryId { get; init; }
        public long? TaxRateId { get; init; }
        public long Quantity { get; init; }
        public long CostValue { get; init; }
        public bool HasStock { get; init; }
        public bool HasInternalBarcode { get; init; }
    }
}
