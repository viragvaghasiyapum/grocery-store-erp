using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Messages;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using GroceryErp.Inventory.Contracts;

namespace GroceryErp.Catalog;

public sealed partial class CatalogService(DbExecutor database, IAuditLog audit, ISettings settings)
{
    // Only the choice of query plan is cached. Row IDs and product fields are
    // always read afresh, so edits in another process cannot return stale rows.
    private readonly object _searchPlanGate = new();
    private readonly Dictionary<string, (DateTime ExpiresUtc, bool Common)> _searchPlans = new(StringComparer.Ordinal);

    public Task<string> SuggestSkuAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection =>
        {
            long next = connection.ExecuteScalar<long>("SELECT COALESCE(MAX(id), 0) + 1 FROM product");
            while (connection.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM product WHERE sku=@sku COLLATE NOCASE)",
                new { sku = $"P{next:D6}" }) != 0) next++;
            return $"P{next:D6}";
        }, cancellationToken);

    public Task<IReadOnlyList<MasterItem>> GetCategoriesAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<MasterItem>)connection.Query<MasterItem>("""
            SELECT id AS Id, '' AS Code, name AS Name, name_local AS NameLocal, is_active AS IsActive,
                   parent_id AS ParentId, expiry_alert_days AS ExpiryAlertDays
            FROM category ORDER BY sort_order, name COLLATE NOCASE
            """).AsList(), cancellationToken);

    public Task<IReadOnlyList<MasterItem>> GetUnitsAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<MasterItem>)connection.Query<MasterItem>("""
            SELECT id AS Id, code AS Code, name AS Name, name_local AS NameLocal, 1 AS IsActive
            FROM uom ORDER BY name COLLATE NOCASE
            """).AsList(), cancellationToken);

    public Task<IReadOnlyList<UnitItem>> GetUnitDetailsAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<UnitItem>)connection.Query<UnitItem>("""
            SELECT id AS Id, code AS Code, name AS Name, name_local AS NameLocal,
                   allow_fraction AS AllowFraction FROM uom ORDER BY name COLLATE NOCASE
            """).AsList(), cancellationToken);

    public Task<IReadOnlyList<MasterItem>> GetBrandsAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<MasterItem>)connection.Query<MasterItem>("""
            SELECT id AS Id, '' AS Code, name AS Name, NULL AS NameLocal, is_active AS IsActive
            FROM brand ORDER BY name COLLATE NOCASE
            """).AsList(), cancellationToken);

    public Task<IReadOnlyList<TaxRateItem>> GetTaxRatesAsync(CancellationToken cancellationToken = default) =>
        database.ReadAsync(connection => (IReadOnlyList<TaxRateItem>)connection.Query<TaxRateRow>("""
            SELECT id AS Id, name AS Name, rate_pct AS RateScaled, is_active AS IsActive
            FROM tax_rate ORDER BY rate_pct
            """).Select(static row => new TaxRateItem(row.Id, row.Name, Fx3.FromScaled(row.RateScaled), row.IsActive)).ToList(), cancellationToken);

    public Task<ProductDetails> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return database.ReadAsync(connection =>
        {
            ProductRow? row = connection.QuerySingleOrDefault<ProductRow>("""
                SELECT id AS Id, sku AS Sku, name AS Name, name_local AS NameLocal,
                       search_terms AS SearchTerms, category_id AS CategoryId, brand_id AS BrandId,
                       uom_id AS UomId, hsn_code AS HsnCode, tax_rate_id AS TaxRateId,
                       price_includes_tax AS PriceIncludesTax, is_loose AS IsLoose, plu_code AS PluCode,
                       track_expiry AS TrackExpiry, default_shelf_life_days AS DefaultShelfLifeDays,
                       expiry_alert_days AS ExpiryAlertDays, reorder_level AS ReorderLevel,
                       default_selling_price AS DefaultSellingPrice, default_mrp AS DefaultMrp,
                       is_active AS IsActive,product_class AS ProductClass
                FROM product WHERE id=@id
                """, new { id });
            if (row is null) throw new DomainException(ErrorCode.NotFound, "product", id);
            IReadOnlyCollection<ProductBarcodeInput> barcodes = connection.Query<BarcodeEditRow>("""
                SELECT barcode AS Barcode, pack_qty AS PackQuantity, symbology AS Symbology,
                       is_primary AS IsPrimary, source AS Source
                FROM product_barcode WHERE product_id=@id AND lot_id IS NULL ORDER BY is_primary DESC, id
                """, new { id }).Select(static barcode => new ProductBarcodeInput(
                    barcode.Barcode, Quantity.FromScaled(barcode.PackQuantity), barcode.Symbology, barcode.IsPrimary, barcode.Source)).ToList();
            return new ProductDetails(row.Id, new ProductInput(
                row.Sku, row.Name, row.NameLocal, row.SearchTerms, row.CategoryId, row.BrandId,
                row.UomId, row.HsnCode, row.TaxRateId, row.PriceIncludesTax, row.IsLoose, row.PluCode,
                row.TrackExpiry, row.DefaultShelfLifeDays, row.ExpiryAlertDays,
                Quantity.FromScaled(row.ReorderLevel),
                row.DefaultSellingPrice is null ? null : Money.FromScaled(row.DefaultSellingPrice.Value),
                row.DefaultMrp is null ? null : Money.FromScaled(row.DefaultMrp.Value), barcodes)
            { ProductClass = row.ProductClass }, row.IsActive);
        }, cancellationToken);
    }

    public Task<long> CreateCategoryAsync(CategoryInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        string name = CatalogText.Required(input.Name, 80, nameof(input.Name));
        string? localName = CatalogText.Optional(input.NameLocal, 80);
        if (input.ExpiryAlertDays < 0) throw new DomainException(ErrorCode.Validation, "Expiry alert days cannot be negative.");
        return database.WriteAsync(transaction =>
        {
            int depth = CategoryDepth(transaction, input.ParentId);
            if (depth >= 3) throw new DomainException(ErrorCode.Validation, "Categories can be at most three levels deep.");
            long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO category(parent_id,name,name_local,expiry_alert_days)
                VALUES (@ParentId,@name,@localName,@ExpiryAlertDays) RETURNING id
                """, new { input.ParentId, name, localName, input.ExpiryAlertDays }, transaction.Transaction);
            audit.Write(transaction, "category", id, "CREATE", new { name, input.ParentId });
            return id;
        }, cancellationToken);
    }

    public Task<long> CreateBrandAsync(string name, CancellationToken cancellationToken = default)
    {
        string normalized = CatalogText.Required(name, 80, nameof(name));
        return database.WriteAsync(transaction =>
        {
            long id = transaction.Connection.ExecuteScalar<long>(
                "INSERT INTO brand(name) VALUES (@normalized) RETURNING id", new { normalized }, transaction.Transaction);
            audit.Write(transaction, "brand", id, "CREATE", new { name = normalized });
            return id;
        }, cancellationToken);
    }

    public Task UpdateCategoryAsync(long id, CategoryInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        string name = CatalogText.Required(input.Name, 80, nameof(input.Name));
        string? localName = CatalogText.Optional(input.NameLocal, 80);
        if (id == input.ParentId) throw new DomainException(ErrorCode.Validation, "A category cannot be its own parent.");
        if (input.ExpiryAlertDays < 0) throw new DomainException(ErrorCode.Validation, "Expiry alert days cannot be negative.");
        return database.WriteAsync(transaction =>
        {
            int depth = CategoryDepth(transaction, input.ParentId);
            int subtreeHeight = transaction.Connection.ExecuteScalar<int>("""
                WITH RECURSIVE descendants(id,depth) AS (
                    SELECT id,1 FROM category WHERE id=@id
                    UNION ALL SELECT c.id,d.depth+1 FROM category c JOIN descendants d ON c.parent_id=d.id
                ) SELECT COALESCE(MAX(depth),1) FROM descendants
                """, new { id }, transaction.Transaction);
            if (depth + subtreeHeight > 3 || HasDescendant(transaction, id, input.ParentId))
                throw new DomainException(ErrorCode.Validation, "The category hierarchy is invalid or exceeds three levels.");
            int changed = transaction.Connection.Execute("""
                UPDATE category SET parent_id=@ParentId,name=@name,name_local=@localName,
                    expiry_alert_days=@ExpiryAlertDays WHERE id=@id
                """, new { id, input.ParentId, name, localName, input.ExpiryAlertDays }, transaction.Transaction);
            if (changed == 0) throw new DomainException(ErrorCode.NotFound, "category", id);
            audit.Write(transaction, "category", id, "UPDATE", new { name, input.ParentId });
        }, cancellationToken);
    }

    public Task UpdateBrandAsync(long id, string name, CancellationToken cancellationToken = default)
    {
        string normalized = CatalogText.Required(name, 80, nameof(name));
        return database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute("UPDATE brand SET name=@normalized WHERE id=@id", new { id, normalized }, transaction.Transaction);
            if (changed == 0) throw new DomainException(ErrorCode.NotFound, "brand", id);
            audit.Write(transaction, "brand", id, "UPDATE", new { name = normalized });
        }, cancellationToken);
    }

    public Task<long> CreateUnitAsync(string code, string name, string? localName, bool allowFraction, CancellationToken cancellationToken = default)
    {
        string normalizedCode = CatalogText.Required(code, 10, nameof(code)).ToUpperInvariant();
        string normalizedName = CatalogText.Required(name, 60, nameof(name));
        string? normalizedLocalName = CatalogText.Optional(localName, 60);
        return database.WriteAsync(transaction =>
        {
            long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO uom(code,name,name_local,allow_fraction)
                VALUES (@normalizedCode,@normalizedName,@normalizedLocalName,@fraction) RETURNING id
                """, new { normalizedCode, normalizedName, normalizedLocalName, fraction = allowFraction ? 1 : 0 }, transaction.Transaction);
            audit.Write(transaction, "uom", id, "CREATE", new { code = normalizedCode, name = normalizedName, allowFraction });
            return id;
        }, cancellationToken);
    }

    public Task UpdateUnitAsync(long id, string code, string name, string? localName, bool allowFraction, CancellationToken cancellationToken = default)
    {
        string normalizedCode = CatalogText.Required(code, 10, nameof(code)).ToUpperInvariant();
        string normalizedName = CatalogText.Required(name, 60, nameof(name));
        string? normalizedLocalName = CatalogText.Optional(localName, 60);
        return database.WriteAsync(transaction =>
        {
            bool? current = transaction.Connection.QuerySingleOrDefault<bool?>(
                "SELECT allow_fraction FROM uom WHERE id=@id", new { id }, transaction.Transaction);
            if (current is null) throw new DomainException(ErrorCode.NotFound, "unit", id);
            if (current != allowFraction && transaction.Connection.ExecuteScalar<long>(
                    "SELECT EXISTS(SELECT 1 FROM product WHERE uom_id=@id)", new { id }, transaction.Transaction) != 0)
                throw new DomainException(ErrorCode.Validation, "Fractional quantity cannot change after a unit is used.");
            transaction.Connection.Execute("""
                UPDATE uom SET code=@normalizedCode,name=@normalizedName,name_local=@normalizedLocalName,
                    allow_fraction=@fraction WHERE id=@id
                """, new { id, normalizedCode, normalizedName, normalizedLocalName, fraction = allowFraction ? 1 : 0 }, transaction.Transaction);
            audit.Write(transaction, "uom", id, "UPDATE", new { code = normalizedCode, name = normalizedName, allowFraction });
        }, cancellationToken);
    }

    public Task<long> CreateTaxRateAsync(string name, decimal rate, CancellationToken cancellationToken = default)
    {
        string normalizedName = CatalogText.Required(name, 60, nameof(name));
        if (rate < 0 || rate > 100) throw new DomainException(ErrorCode.Validation, "Tax rate must be between 0 and 100.");
        return database.WriteAsync(transaction =>
        {
            long id = transaction.Connection.ExecuteScalar<long>(
                "INSERT INTO tax_rate(name,rate_pct) VALUES (@normalizedName,@scaled) RETURNING id",
                new { normalizedName, scaled = Fx3.ToScaled(rate) }, transaction.Transaction);
            audit.Write(transaction, "tax_rate", id, "CREATE", new { name = normalizedName, rate });
            return id;
        }, cancellationToken);
    }

    public Task UpdateTaxRateAsync(long id, string name, decimal rate, bool isActive, CancellationToken cancellationToken = default)
    {
        string normalizedName = CatalogText.Required(name, 60, nameof(name));
        if (rate < 0 || rate > 100) throw new DomainException(ErrorCode.Validation, "Tax rate must be between 0 and 100.");
        return database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute("""
                UPDATE tax_rate SET name=@normalizedName,rate_pct=@scaled,is_active=@active WHERE id=@id
                """, new { id, normalizedName, scaled = Fx3.ToScaled(rate), active = isActive ? 1 : 0 }, transaction.Transaction);
            if (changed == 0) throw new DomainException(ErrorCode.NotFound, "tax rate", id);
            audit.Write(transaction, "tax_rate", id, "UPDATE", new { name = normalizedName, rate, isActive });
        }, cancellationToken);
    }

    public Task<long> CreateAsync(ProductInput input, CancellationToken cancellationToken = default)
    {
        ValidatedProduct product = Validate(input);
        return database.WriteAsync(transaction =>
        {
            ValidateUnit(transaction, null, input.UomId, input.IsLoose);
            return CreateProduct(transaction, product);
        }, cancellationToken);
    }

    public async Task<long> CreateWithDirectLotAsync(ProductInput input, NewProductLotInput lot,
        ILotQuantities stock, DocNumberService numbers, PeriodGuard periods, string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lot);
        ValidatedProduct product = Validate(input);
        if (lot.Quantity <= Quantity.Zero || lot.UnitCost < Money.Zero ||
            lot.SellingPrice < Money.Zero || lot.Mrp < Money.Zero ||
            lot.Mrp is not null && lot.SellingPrice > lot.Mrp.Value ||
            input.TrackExpiry && lot.ExpiryDate is null ||
            lot.ExpiryDate is not null && lot.ExpiryDate < lot.ReceivedDate ||
            string.IsNullOrWhiteSpace(lot.Reason) || lot.Reason.Length > 500 ||
            lot.BatchNumber?.Length > 120)
            throw new DomainException(ErrorCode.Validation, "Invalid first lot.");
        periods.EnsureOpen(lot.ReceivedDate);
        long id = await database.WriteAsync(transaction =>
        {
            OperationRef? replay = OperationGuard.Claim(transaction, operationId, OpType.DirectEntry);
            if (replay?.RefId is long previous) return previous;
            periods.EnsureOpen(transaction, lot.ReceivedDate);
            ValidateUnit(transaction, null, input.UomId, input.IsLoose);
            if (transaction.Connection.ExecuteScalar<long>(
                "SELECT allow_fraction FROM uom WHERE id=@id", new { id = input.UomId }, transaction.Transaction) == 0 &&
                !lot.Quantity.IsWhole)
                throw new DomainException(ErrorCode.FractionalQtyNotAllowed);
            long productId = CreateProduct(transaction, product);
            string lotCode = numbers.Next(transaction, DocType.Lot, lot.ReceivedDate);
            long lotId = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO product_lot(product_id,lot_code,batch_no,received_date,expiry_date,
                    unit_cost,mrp,selling_price,qty_received,direct_reason)
                VALUES (@productId,@lotCode,@batch,@date,@expiry,@cost,@mrp,@price,@quantity,@reason)
                RETURNING id
                """, new
            {
                productId,
                lotCode,
                batch = lot.BatchNumber?.Trim(),
                date = lot.ReceivedDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                expiry = lot.ExpiryDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                cost = lot.UnitCost.Scaled,
                mrp = lot.Mrp?.Scaled,
                price = lot.SellingPrice.Scaled,
                quantity = lot.Quantity.Scaled,
                reason = lot.Reason.Trim()
            }, transaction.Transaction);
            long originId = transaction.Connection.ExecuteScalar<long>(
                "SELECT id FROM direct_stock_origin WHERE lot_id=@lotId", new { lotId }, transaction.Transaction);
            stock.Change(transaction, new(lot.ReceivedDate, productId, lotId, LotChangeKind.DirectEntry, lot.Quantity));
            audit.Write(transaction, "direct_stock_origin", originId, "CREATE", new { lotId });
            OperationGuard.Complete(transaction, operationId, "product", productId);
            return productId;
        }, cancellationToken);
        WeakReferenceMessenger.Default.Send(new StockChangedMessage([id]));
        return id;
    }

    private long CreateProduct(DbTx transaction, ValidatedProduct product)
    {
        long id = transaction.Connection.ExecuteScalar<long>("""
                INSERT INTO product(
                    sku, name, name_local, search_terms, category_id, brand_id, uom_id, hsn_code,
                    tax_rate_id, price_includes_tax, is_loose, plu_code, track_expiry,
                    default_shelf_life_days, expiry_alert_days, reorder_level,
                    default_selling_price, default_mrp, product_class)
                VALUES (
                    @Sku, @Name, @NameLocal, @SearchTerms, @CategoryId, @BrandId, @UomId, @HsnCode,
                    @TaxRateId, @PriceIncludesTax, @IsLoose, @PluCode, @TrackExpiry,
                    @DefaultShelfLifeDays, @ExpiryAlertDays, @ReorderLevel,
                    @DefaultSellingPrice, @DefaultMrp, @ProductClass)
                RETURNING id
                """, product, transaction.Transaction);
        InsertBarcodes(transaction, id, product.Barcodes);
        audit.Write(transaction, "product", id, "CREATE", new { product.Sku, product.Name });
        return id;
    }

    public Task<BarcodeOwner?> AttachBarcodeAsync(long productId, string rawBarcode, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(productId);
        string barcode = CatalogText.Required(GroceryErp.Core.InputNormalizer.Normalize(rawBarcode), 64, nameof(rawBarcode));
        if (barcode.All(char.IsAsciiDigit) && barcode.Length is 8 or 12 or 13 or 14 && !Ean.IsValid(barcode))
            throw new DomainException(ErrorCode.InvalidBarcode, barcode);
        return database.WriteAsync(transaction =>
        {
            long active = transaction.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM product WHERE id=@productId AND is_active=1", new { productId }, transaction.Transaction);
            if (active == 0) throw new DomainException(ErrorCode.NotFound, "product", productId);
            BarcodeOwner? owner = transaction.Connection.QuerySingleOrDefault<BarcodeOwner>("""
                SELECT p.id AS ProductId, p.name AS ProductName
                FROM product_barcode b JOIN product p ON p.id=b.product_id
                WHERE b.barcode=@barcode
                """, new { barcode }, transaction.Transaction);
            if (owner is not null) return owner.ProductId == productId ? null : owner;
            string symbology = barcode.Length switch
            {
                8 => "EAN8",
                12 => "UPCA",
                13 => "EAN13",
                _ => "CODE128",
            };
            transaction.Connection.Execute("""
                INSERT INTO product_barcode(barcode, product_id, pack_qty, symbology, source, is_primary)
                VALUES (@barcode, @productId, 1000, @symbology, 'MANUFACTURER',
                    CASE WHEN EXISTS(SELECT 1 FROM product_barcode WHERE product_id=@productId AND is_primary=1) THEN 0 ELSE 1 END)
                """, new { barcode, productId, symbology }, transaction.Transaction);
            audit.Write(transaction, "product", productId, "BARCODE_ADD", new { barcode });
            return (BarcodeOwner?)null;
        }, cancellationToken);
    }

    public Task UpdateAsync(long id, ProductInput input, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ValidatedProduct product = Validate(input);
        return database.WriteAsync(transaction =>
        {
            ValidateUnit(transaction, id, input.UomId, input.IsLoose);
            long? currentUom = transaction.Connection.QuerySingleOrDefault<long?>(
                "SELECT uom_id FROM product WHERE id = @id", new { id }, transaction.Transaction);
            if (currentUom is null) throw new DomainException(ErrorCode.NotFound, "product", id);
            if (transaction.Connection.ExecuteScalar<long>("""
                SELECT EXISTS(SELECT 1 FROM product p WHERE p.id=@id AND p.product_class<>@productClass
                    AND EXISTS(SELECT 1 FROM product_lot l WHERE l.product_id=p.id AND l.qty_on_hand<>0))
                """, new { id, productClass = product.ProductClass }, transaction.Transaction) != 0)
                throw new DomainException(ErrorCode.Validation,
                    "Products with stock cannot change classification. Consume or return stock first.");

            transaction.Connection.Execute("""
                UPDATE product SET
                    sku=@Sku, name=@Name, name_local=@NameLocal, search_terms=@SearchTerms,
                    category_id=@CategoryId, brand_id=@BrandId, uom_id=@UomId, hsn_code=@HsnCode,
                    tax_rate_id=@TaxRateId, price_includes_tax=@PriceIncludesTax, is_loose=@IsLoose,
                    plu_code=@PluCode, track_expiry=@TrackExpiry,
                    default_shelf_life_days=@DefaultShelfLifeDays, expiry_alert_days=@ExpiryAlertDays,
                    reorder_level=@ReorderLevel, default_selling_price=@DefaultSellingPrice,
                    default_mrp=@DefaultMrp, product_class=@ProductClass,
                    updated_at=datetime('now','localtime')
                WHERE id=@id
                """, new
            {
                id,
                product.Sku,
                product.Name,
                product.NameLocal,
                product.SearchTerms,
                product.CategoryId,
                product.BrandId,
                product.UomId,
                product.HsnCode,
                product.TaxRateId,
                product.PriceIncludesTax,
                product.IsLoose,
                product.PluCode,
                product.TrackExpiry,
                product.DefaultShelfLifeDays,
                product.ExpiryAlertDays,
                product.ReorderLevel,
                product.DefaultSellingPrice,
                product.DefaultMrp,
                product.ProductClass,
            }, transaction.Transaction);
            transaction.Connection.Execute("DELETE FROM product_barcode WHERE product_id=@id AND lot_id IS NULL", new { id }, transaction.Transaction);
            InsertBarcodes(transaction, id, product.Barcodes);
            audit.Write(transaction, "product", id, "UPDATE", new { product.Sku, product.Name });
        }, cancellationToken);
    }

    public Task<ProductRemoval> RemoveAsync(long id, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            long exists = transaction.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM product WHERE id=@id", new { id }, transaction.Transaction);
            if (exists == 0) throw new DomainException(ErrorCode.NotFound, "product", id);
            return Remove(transaction, id);
        }, cancellationToken);

    // Posted documents carry their own snapshots, so history never blocks deletion.
    // Stock, an unfinished bill or a draft purchase still needs the live product,
    // which is then archived instead.
    private ProductRemoval Remove(DbTx transaction, long id)
    {
        bool inUse = transaction.Connection.ExecuteScalar<long>("""
            SELECT EXISTS(SELECT 1 FROM product_lot WHERE product_id=@id AND qty_on_hand<>0)
                OR EXISTS(SELECT 1 FROM sale s CROSS JOIN sale_line sl
                          WHERE s.status IN ('HELD','DRAFT') AND sl.sale_id=s.id AND sl.product_id=@id)
                OR EXISTS(SELECT 1 FROM purchase p CROSS JOIN purchase_line pl
                          WHERE p.status='DRAFT' AND pl.purchase_id=p.id AND pl.product_id=@id)
            """, new { id }, transaction.Transaction) != 0;
        if (!inUse)
        {
            long[] lots = transaction.Connection.Query<long>(
                "SELECT id FROM product_lot WHERE product_id=@id", new { id }, transaction.Transaction).ToArray();
            transaction.Connection.Execute("DELETE FROM product_barcode WHERE product_id=@id", new { id }, transaction.Transaction);
            transaction.Connection.Execute("DELETE FROM product_lot WHERE product_id=@id AND qty_on_hand=0",
                new { id }, transaction.Transaction);
            transaction.Connection.Execute("DELETE FROM product WHERE id=@id", new { id }, transaction.Transaction);
            audit.Write(transaction, "product", id, "DELETE", new { zeroLots = lots });
            return ProductRemoval.Deleted;
        }
        transaction.Connection.Execute(
            "UPDATE product SET is_active=0, updated_at=datetime('now','localtime') WHERE id=@id",
            new { id }, transaction.Transaction);
        audit.Write(transaction, "product", id, "ARCHIVE", null);
        return ProductRemoval.Archived;
    }

    public Task RestoreAsync(long id, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            int changed = transaction.Connection.Execute(
                "UPDATE product SET is_active=1, updated_at=datetime('now','localtime') WHERE id=@id AND is_active=0",
                new { id }, transaction.Transaction);
            if (changed == 0) throw new DomainException(ErrorCode.NotFound, "archived product", id);
            audit.Write(transaction, "product", id, "RESTORE", null);
        }, cancellationToken);

    public Task SetActiveAsync(long id, bool active, bool expectedActive,
        CancellationToken cancellationToken = default) => database.WriteAsync(transaction =>
    {
        if (active == expectedActive) return;
        int changed = transaction.Connection.Execute("""
            UPDATE product SET is_active=@active, updated_at=datetime('now','localtime')
            WHERE id=@id AND is_active=@expectedActive
            """, new { id, active = active ? 1 : 0, expectedActive = expectedActive ? 1 : 0 },
            transaction.Transaction);
        if (changed == 0)
        {
            long exists = transaction.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM product WHERE id=@id", new { id }, transaction.Transaction);
            if (exists == 0) throw new DomainException(ErrorCode.NotFound, "product", id);
            throw new DomainException(ErrorCode.Validation, "This product changed since it was loaded. Refresh it and try again.");
        }
        audit.Write(transaction, "product", id, active ? "RESTORE" : "ARCHIVE", null);
    }, cancellationToken);

    public Task<string> GenerateInternalBarcodeAsync(long productId, string prefix, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            long exists = transaction.Connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM product WHERE id=@productId", new { productId }, transaction.Transaction);
            if (exists == 0) throw new DomainException(ErrorCode.NotFound, "product", productId);
            return EnsureInternalBarcode(transaction, productId, prefix);
        }, cancellationToken);

    private string EnsureInternalBarcode(DbTx transaction, long productId, string prefix)
    {
        string? existingBarcode = transaction.Connection.QueryFirstOrDefault<string>(
            "SELECT barcode FROM product_barcode WHERE product_id=@productId AND lot_id IS NULL AND source='INTERNAL' ORDER BY id LIMIT 1",
            new { productId }, transaction.Transaction);
        if (existingBarcode is not null) return existingBarcode;
        string barcode = Ean.Internal(prefix, productId);
        transaction.Connection.Execute("""
            INSERT INTO product_barcode(barcode, product_id, pack_qty, symbology, source, is_primary)
            VALUES (@barcode, @productId, 1000, 'EAN13', 'INTERNAL',
                CASE WHEN EXISTS(SELECT 1 FROM product_barcode WHERE product_id=@productId AND is_primary=1) THEN 0 ELSE 1 END)
            """, new { barcode, productId }, transaction.Transaction);
        audit.Write(transaction, "product", productId, "BARCODE_ADD", new { barcode });
        return barcode;
    }

    public Task<string> EnsureLotBarcodeAsync(long lotId, string prefix, CancellationToken cancellationToken = default) =>
        database.WriteAsync(transaction =>
        {
            long? productId = transaction.Connection.QuerySingleOrDefault<long?>(
                "SELECT product_id FROM product_lot WHERE id=@lotId AND is_active=1", new { lotId }, transaction.Transaction);
            if (productId is null) throw new DomainException(ErrorCode.NotFound, "lot", lotId);
            string? existing = transaction.Connection.QueryFirstOrDefault<string>(
                "SELECT barcode FROM product_barcode WHERE lot_id=@lotId AND source='INTERNAL' ORDER BY id LIMIT 1",
                new { lotId }, transaction.Transaction);
            if (existing is not null) return existing;
            string barcode = Ean.Internal(prefix, lotId);
            transaction.Connection.Execute("""
                INSERT INTO product_barcode(barcode, product_id, lot_id, pack_qty, symbology, source, is_primary)
                VALUES (@barcode, @productId, @lotId, 1000, 'EAN13', 'INTERNAL', 0)
                """, new { barcode, productId, lotId }, transaction.Transaction);
            audit.Write(transaction, "product_lot", lotId, "BARCODE_ADD", new { barcode });
            return barcode;
        }, cancellationToken);

    private ValidatedProduct Validate(ProductInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string sku = CatalogText.Required(input.Sku, 20, nameof(input.Sku));
        string name = CatalogText.Required(input.Name, 120, nameof(input.Name));
        string? hsn = CatalogText.Optional(input.HsnCode, 8);
        if (hsn is not null && (hsn.Length is not (4 or 6 or 8) || hsn.Any(static character => !char.IsAsciiDigit(character))))
            throw new DomainException(ErrorCode.Validation, "HSN must contain 4, 6, or 8 digits.");
        if (settings.Current.TaxMode == "REGULAR" && (hsn is null || input.TaxRateId is null))
            throw new DomainException(ErrorCode.Validation, "HSN and GST rate are required in regular GST mode.");
        if (input.DefaultMrp is not null && input.DefaultSellingPrice > input.DefaultMrp)
            throw new DomainException(ErrorCode.PriceAboveMrp);
        if (input.ReorderLevel.Value < 0 || input.DefaultSellingPrice?.Value < 0 || input.DefaultMrp?.Value < 0)
            throw new DomainException(ErrorCode.Validation, "Prices and quantities cannot be negative.");
        if (input.IsLoose && string.IsNullOrWhiteSpace(input.PluCode))
            throw new DomainException(ErrorCode.Validation, "Loose products require a PLU code.");
        if (input.ProductClass is not ("SALEABLE" or "INTERNAL"))
            throw new DomainException(ErrorCode.Validation, "Choose a product classification.");
        if (input.DefaultShelfLifeDays <= 0 || input.ExpiryAlertDays < 0)
            throw new DomainException(ErrorCode.Validation, "Shelf life must be positive and expiry alert days cannot be negative.");

        List<ValidatedBarcode> barcodes = [];
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProductBarcodeInput barcodeInput in input.Barcodes ?? [])
        {
            string barcode = CatalogText.Required(barcodeInput.Barcode, 64, nameof(barcodeInput.Barcode));
            if (!unique.Add(barcode)) throw new DomainException(ErrorCode.BarcodeExists, barcode);
            if (barcode.All(char.IsAsciiDigit) && barcode.Length is 8 or 12 or 13 or 14 && !Ean.IsValid(barcode))
                throw new DomainException(ErrorCode.InvalidBarcode, barcode);
            if (barcodeInput.PackQuantity.Value <= 0) throw new DomainException(ErrorCode.Validation, "Barcode pack quantity must be positive.");
            string symbology = barcodeInput.Symbology.ToUpperInvariant();
            if (symbology is not ("EAN13" or "EAN8" or "UPCA" or "CODE128" or "OTHER"))
                throw new DomainException(ErrorCode.Validation, "Unsupported barcode symbology.");
            if (barcodeInput.Source is not ("MANUFACTURER" or "INTERNAL"))
                throw new DomainException(ErrorCode.Validation, "Unsupported barcode source.");
            barcodes.Add(new(barcode, barcodeInput.PackQuantity.Scaled, symbology, barcodeInput.IsPrimary, barcodeInput.Source));
        }
        if (barcodes.Count(static item => item.IsPrimary) > 1)
            throw new DomainException(ErrorCode.Validation, "Only one barcode can be primary.");

        return new(
            sku, name, CatalogText.Optional(input.NameLocal, 120), CatalogText.Optional(input.SearchTerms, 500),
            input.CategoryId, input.BrandId, input.UomId, hsn, input.TaxRateId,
            input.PriceIncludesTax ? 1 : 0, input.IsLoose ? 1 : 0, CatalogText.Optional(input.PluCode, 30),
            input.TrackExpiry ? 1 : 0, input.DefaultShelfLifeDays, input.ExpiryAlertDays,
            input.ReorderLevel.Scaled, input.DefaultSellingPrice?.Scaled, input.DefaultMrp?.Scaled,
            input.ProductClass, barcodes);
    }

    private static void InsertBarcodes(DbTx transaction, long productId, IEnumerable<ValidatedBarcode> barcodes)
    {
        foreach (ValidatedBarcode barcode in barcodes)
            transaction.Connection.Execute("""
                INSERT INTO product_barcode(barcode, product_id, pack_qty, symbology, source, is_primary)
                VALUES (@Barcode, @productId, @PackQuantity, @Symbology, @Source, @IsPrimary)
                """, new { barcode.Barcode, productId, barcode.PackQuantity, barcode.Symbology, barcode.IsPrimary, barcode.Source }, transaction.Transaction);
    }

    private static int CategoryDepth(DbTx transaction, long? parentId)
    {
        if (parentId is null) return 0;
        int? depth = transaction.Connection.QuerySingleOrDefault<int?>("""
            WITH RECURSIVE ancestors(id,parent_id,depth) AS (
                SELECT id,parent_id,1 FROM category WHERE id=@parentId
                UNION ALL
                SELECT c.id,c.parent_id,a.depth+1 FROM category c JOIN ancestors a ON c.id=a.parent_id
            ) SELECT MAX(depth) FROM ancestors
            """, new { parentId }, transaction.Transaction);
        if (depth is null) throw new DomainException(ErrorCode.NotFound, "parent category", parentId);
        return depth.Value;
    }

    private static bool HasDescendant(DbTx transaction, long id, long? candidateParentId)
    {
        if (candidateParentId is null) return false;
        return transaction.Connection.ExecuteScalar<long>("""
            WITH RECURSIVE descendants(id) AS (
                SELECT id FROM category WHERE parent_id=@id
                UNION ALL SELECT c.id FROM category c JOIN descendants d ON c.parent_id=d.id
            ) SELECT EXISTS(SELECT 1 FROM descendants WHERE id=@candidateParentId)
            """, new { id, candidateParentId }, transaction.Transaction) != 0;
    }

    internal static void ValidateUnit(DbTx transaction, long? productId, long unitId, bool isLoose)
    {
        bool? fractional = transaction.Connection.QuerySingleOrDefault<bool?>(
            "SELECT allow_fraction FROM uom WHERE id=@unitId", new { unitId }, transaction.Transaction);
        if (fractional is null) throw new DomainException(ErrorCode.NotFound, "unit", unitId);
        if (isLoose && !fractional.Value) throw new DomainException(ErrorCode.Validation, "Loose products require a fractional unit.");
        if (productId is not null && transaction.Connection.ExecuteScalar<long>("""
            SELECT EXISTS(SELECT 1 FROM product p WHERE p.id=@productId AND p.uom_id<>@unitId
                AND EXISTS(SELECT 1 FROM product_lot l WHERE l.product_id=p.id))
            """, new { productId, unitId }, transaction.Transaction) != 0)
            throw new DomainException(ErrorCode.Validation, "A product's unit cannot change after stock lots exist.");
    }

    private sealed record ValidatedBarcode(string Barcode, long PackQuantity, string Symbology, bool IsPrimary, string Source);
    private sealed record ValidatedProduct(
        string Sku, string Name, string? NameLocal, string? SearchTerms, long CategoryId, long? BrandId,
        long UomId, string? HsnCode, long? TaxRateId, int PriceIncludesTax, int IsLoose, string? PluCode,
        int TrackExpiry, int? DefaultShelfLifeDays, int? ExpiryAlertDays, long ReorderLevel,
        long? DefaultSellingPrice, long? DefaultMrp, string ProductClass,
        IReadOnlyCollection<ValidatedBarcode> Barcodes);

    private sealed class TaxRateRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public long RateScaled { get; init; }
        public bool IsActive { get; init; }
    }

    private sealed class BarcodeEditRow
    {
        public string Barcode { get; init; } = string.Empty;
        public long PackQuantity { get; init; }
        public string Symbology { get; init; } = string.Empty;
        public bool IsPrimary { get; init; }
        public string Source { get; init; } = "MANUFACTURER";
    }

    private sealed class ProductRow
    {
        public long Id { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? NameLocal { get; init; }
        public string? SearchTerms { get; init; }
        public long CategoryId { get; init; }
        public long? BrandId { get; init; }
        public long UomId { get; init; }
        public string? HsnCode { get; init; }
        public long? TaxRateId { get; init; }
        public bool PriceIncludesTax { get; init; }
        public bool IsLoose { get; init; }
        public string? PluCode { get; init; }
        public bool TrackExpiry { get; init; }
        public int? DefaultShelfLifeDays { get; init; }
        public int? ExpiryAlertDays { get; init; }
        public long ReorderLevel { get; init; }
        public long? DefaultSellingPrice { get; init; }
        public long? DefaultMrp { get; init; }
        public bool IsActive { get; init; }
        public string ProductClass { get; init; } = "SALEABLE";
    }

}
