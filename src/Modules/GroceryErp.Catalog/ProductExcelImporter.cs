using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using Dapper;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;

namespace GroceryErp.Catalog;

public sealed record ProductImportRow(
    int RowNumber,
    string Sku,
    string Name,
    string? NameLocal,
    string? Aliases,
    string Category,
    string? Subcategory,
    string? Brand,
    string Unit,
    string? Hsn,
    decimal? GstRate,
    string? Barcode,
    decimal? SellingPrice,
    decimal? Mrp,
    decimal ReorderLevel,
    int? ShelfLifeDays,
    bool IsLoose,
    string? Plu,
    bool TrackExpiry,
    IReadOnlyList<string> Errors)
{
    public bool CanImport => Errors.Count == 0;
    public string ErrorText => string.Join("; ", Errors);
}

public sealed record ProductImportPreview(IReadOnlyList<ProductImportRow> Rows)
{
    public int ValidCount => Rows.Count(static row => row.CanImport);
    public int ErrorCount => Rows.Count - ValidCount;
}

public sealed record ProductImportSummary(int Created, int Updated, int Skipped, bool WasReplay = false);

public sealed class ProductExcelImporter(DbExecutor database, IAuditLog audit, ISettings settings)
{
    public static readonly string[] Headers =
    [
        "SKU*", "Name*", "Local name", "Aliases", "Category*", "Subcategory", "Brand", "Unit*",
        "HSN", "GST %", "Barcode", "Selling price", "MRP", "Reorder level", "Shelf life days",
        "Loose (Y/N)", "PLU", "Track expiry (Y/N)",
    ];

    public static void WriteTemplate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var workbook = new XLWorkbook();
        IXLWorksheet sheet = workbook.AddWorksheet("Products");
        for (int column = 0; column < Headers.Length; column++)
            sheet.Cell(1, column + 1).Value = Headers[column];
        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
        workbook.SaveAs(path);
    }

    public async Task<ProductImportPreview> DryRunAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ImportLookups lookups = await database.ReadAsync(connection =>
        {
            var units = connection.Query<(long Id, string Code)>("SELECT id,code FROM uom")
                .ToDictionary(static item => item.Code, static item => item.Id, StringComparer.OrdinalIgnoreCase);
            var taxes = connection.Query<(long Id, long Rate)>("SELECT id,rate_pct AS Rate FROM tax_rate WHERE is_active=1")
                .ToDictionary(static item => item.Rate, static item => item.Id);
            var barcodes = connection.Query<(string Barcode, string Sku)>("""
                SELECT pb.barcode AS Barcode,p.sku AS Sku FROM product_barcode pb JOIN product p ON p.id=pb.product_id
                """).ToDictionary(static item => item.Barcode, static item => item.Sku, StringComparer.Ordinal);
            return new ImportLookups(units, taxes, barcodes);
        }, cancellationToken);

        return await Task.Run(() => ReadWorkbook(path, lookups), cancellationToken);
    }

    public Task<ProductImportSummary> ImportAsync(
        ProductImportPreview preview,
        string operationId,
        IReadOnlySet<int>? selectedRows = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        List<ProductImportRow> rows = preview.Rows
            .Where(row => row.CanImport && (selectedRows is null || selectedRows.Contains(row.RowNumber))).ToList();
        int skipped = preview.Rows.Count - rows.Count;
        return database.WriteAsync(transaction =>
        {
            if (OperationGuard.Claim(transaction, operationId, OpType.ProductImport) is not null)
                return new ProductImportSummary(0, 0, skipped, true);
            int created = 0;
            int updated = 0;
            foreach (ProductImportRow row in rows)
            {
                long categoryId = FindOrCreateCategory(transaction, row.Category, null);
                if (!string.IsNullOrWhiteSpace(row.Subcategory))
                    categoryId = FindOrCreateCategory(transaction, row.Subcategory, categoryId);
                long? brandId = string.IsNullOrWhiteSpace(row.Brand) ? null : FindOrCreateBrand(transaction, row.Brand);
                long unitId = transaction.Connection.QuerySingle<long>(
                    "SELECT id FROM uom WHERE code=@Unit COLLATE NOCASE", row, transaction.Transaction);
                long? taxRateId = row.GstRate is null ? null : transaction.Connection.QuerySingle<long>(
                    "SELECT id FROM tax_rate WHERE rate_pct=@rate", new { rate = Fx3.ToScaled(row.GstRate.Value) }, transaction.Transaction);
                long? existingId = transaction.Connection.QuerySingleOrDefault<long?>(
                    "SELECT id FROM product WHERE sku=@Sku COLLATE NOCASE", row, transaction.Transaction);
                var values = new
                {
                    row.Sku,
                    row.Name,
                    row.NameLocal,
                    row.Aliases,
                    categoryId,
                    brandId,
                    unitId,
                    row.Hsn,
                    taxRateId,
                    IsLoose = row.IsLoose ? 1 : 0,
                    row.Plu,
                    TrackExpiry = row.TrackExpiry ? 1 : 0,
                    row.ShelfLifeDays,
                    ReorderLevel = Fx3.ToScaled(row.ReorderLevel),
                    SellingPrice = row.SellingPrice is null ? (long?)null : Fx3.ToScaled(row.SellingPrice.Value),
                    Mrp = row.Mrp is null ? (long?)null : Fx3.ToScaled(row.Mrp.Value),
                };
                long productId;
                if (existingId is null)
                {
                    productId = transaction.Connection.ExecuteScalar<long>("""
                        INSERT INTO product(sku,name,name_local,search_terms,category_id,brand_id,uom_id,
                            hsn_code,tax_rate_id,is_loose,plu_code,track_expiry,default_shelf_life_days,
                            reorder_level,default_selling_price,default_mrp)
                        VALUES (@Sku,@Name,@NameLocal,@Aliases,@categoryId,@brandId,@unitId,@Hsn,@taxRateId,
                            @IsLoose,@Plu,@TrackExpiry,@ShelfLifeDays,@ReorderLevel,@SellingPrice,@Mrp)
                        RETURNING id
                        """, values, transaction.Transaction);
                    created++;
                    audit.Write(transaction, "product", productId, "IMPORT_CREATE", new { row.RowNumber, row.Sku });
                }
                else
                {
                    productId = existingId.Value;
                    transaction.Connection.Execute("""
                        UPDATE product SET name=@Name,name_local=@NameLocal,search_terms=@Aliases,
                            category_id=@categoryId,brand_id=@brandId,uom_id=@unitId,hsn_code=@Hsn,
                            tax_rate_id=@taxRateId,is_loose=@IsLoose,plu_code=@Plu,track_expiry=@TrackExpiry,
                            default_shelf_life_days=@ShelfLifeDays,reorder_level=@ReorderLevel,
                            default_selling_price=@SellingPrice,default_mrp=@Mrp,is_active=1,
                            updated_at=datetime('now','localtime') WHERE sku=@Sku COLLATE NOCASE
                        """, values, transaction.Transaction);
                    updated++;
                    audit.Write(transaction, "product", productId, "IMPORT_UPDATE", new { row.RowNumber, row.Sku });
                }
                if (!string.IsNullOrWhiteSpace(row.Barcode))
                    transaction.Connection.Execute("""
                        INSERT INTO product_barcode(barcode,product_id,pack_qty,symbology,source,is_primary)
                        VALUES (@Barcode,@productId,1000,@symbology,'MANUFACTURER',
                            CASE WHEN EXISTS(SELECT 1 FROM product_barcode WHERE product_id=@productId AND is_primary=1) THEN 0 ELSE 1 END)
                        ON CONFLICT(barcode) DO NOTHING
                        """, new
                    {
                        row.Barcode,
                        productId,
                        symbology = row.Barcode.Length == 8 ? "EAN8" : row.Barcode.Length == 12 ? "UPCA" : row.Barcode.Length == 13 ? "EAN13" : "CODE128",
                    }, transaction.Transaction);
            }
            audit.Write(transaction, "product_import", 0, "IMPORT", new { created, updated, skipped });
            OperationGuard.Complete(transaction, operationId, "product_import", created + updated);
            return new ProductImportSummary(created, updated, skipped);
        }, cancellationToken);
    }

    private ProductImportPreview ReadWorkbook(string path, ImportLookups lookups)
    {
        using var workbook = new XLWorkbook(path);
        IXLWorksheet sheet = workbook.Worksheets.First();
        for (int column = 0; column < Headers.Length; column++)
            if (!string.Equals(sheet.Cell(1, column + 1).GetString().Trim(), Headers[column], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Column {column + 1} must be '{Headers[column]}'.");

        int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        var rows = new List<ProductImportRow>(Math.Max(0, lastRow - 1));
        var fileSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileBarcodes = new HashSet<string>(StringComparer.Ordinal);
        for (int rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            string[] cells = Enumerable.Range(1, Headers.Length)
                .Select(column => sheet.Cell(rowNumber, column).GetFormattedString().Trim()).ToArray();
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            var errors = new List<string>();
            string sku = Required(cells[0], "SKU", errors);
            string name = Required(cells[1], "Name", errors);
            string category = Required(cells[4], "Category", errors);
            string unit = Required(cells[7], "Unit", errors);
            if (sku.Length > 20) errors.Add("SKU is longer than 20 characters.");
            if (name.Length > 120) errors.Add("Name is longer than 120 characters.");
            if (sku.Length > 0 && !fileSkus.Add(sku)) errors.Add("Duplicate SKU in this file.");
            if (unit.Length > 0 && !lookups.Units.ContainsKey(unit)) errors.Add($"Unit '{unit}' does not exist.");
            decimal? gst = DecimalValue(cells[9], "GST %", errors);
            if (gst is not null && !lookups.Taxes.ContainsKey(Fx3.ToScaled(gst.Value))) errors.Add($"GST rate {gst:0.###}% does not exist.");
            string? hsn = NullIfEmpty(cells[8]);
            if (settings.Current.TaxMode == "REGULAR" && (hsn is null || gst is null)) errors.Add("HSN and GST % are required in regular GST mode.");
            if (hsn is not null && (hsn.Length is not (4 or 6 or 8) || hsn.Any(static character => !char.IsAsciiDigit(character)))) errors.Add("HSN must contain 4, 6, or 8 digits.");
            string? barcode = NullIfEmpty(cells[10]);
            if (barcode is not null)
            {
                if (!fileBarcodes.Add(barcode)) errors.Add("Duplicate barcode in this file.");
                if (barcode.All(char.IsAsciiDigit) && barcode.Length is 8 or 12 or 13 or 14 && !Ean.IsValid(barcode)) errors.Add("Barcode check digit is invalid.");
                if (lookups.Barcodes.TryGetValue(barcode, out string? ownerSku) && !string.Equals(ownerSku, sku, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Barcode already belongs to SKU {ownerSku}.");
            }
            decimal? sellingPrice = DecimalValue(cells[11], "Selling price", errors);
            decimal? mrp = DecimalValue(cells[12], "MRP", errors);
            decimal reorder = DecimalValue(cells[13], "Reorder level", errors) ?? 0;
            int? shelfLife = IntegerValue(cells[14], "Shelf life days", errors);
            bool loose = YesNo(cells[15], "Loose", false, errors);
            string? plu = NullIfEmpty(cells[16]);
            bool trackExpiry = YesNo(cells[17], "Track expiry", true, errors);
            if (loose && plu is null) errors.Add("Loose products require a PLU.");
            if (sellingPrice < 0 || mrp < 0 || reorder < 0) errors.Add("Prices and reorder level cannot be negative.");
            if (sellingPrice is not null && mrp is not null && sellingPrice > mrp) errors.Add("Selling price cannot exceed MRP.");
            rows.Add(new(rowNumber, sku, name, NullIfEmpty(cells[2]), NullIfEmpty(cells[3]), category,
                NullIfEmpty(cells[5]), NullIfEmpty(cells[6]), unit, hsn, gst, barcode, sellingPrice,
                mrp, reorder, shelfLife, loose, plu, trackExpiry, errors));
        }
        return new(rows);
    }

    private static long FindOrCreateCategory(DbTx transaction, string name, long? parentId)
    {
        long? id = transaction.Connection.QuerySingleOrDefault<long?>("""
            SELECT id FROM category WHERE name=@name COLLATE NOCASE AND COALESCE(parent_id,0)=COALESCE(@parentId,0)
            """, new { name, parentId }, transaction.Transaction);
        return id ?? transaction.Connection.ExecuteScalar<long>(
            "INSERT INTO category(name,parent_id) VALUES (@name,@parentId) RETURNING id",
            new { name, parentId }, transaction.Transaction);
    }

    private static long FindOrCreateBrand(DbTx transaction, string name) =>
        transaction.Connection.QuerySingleOrDefault<long?>(
            "SELECT id FROM brand WHERE name=@name COLLATE NOCASE", new { name }, transaction.Transaction)
        ?? transaction.Connection.ExecuteScalar<long>(
            "INSERT INTO brand(name) VALUES (@name) RETURNING id", new { name }, transaction.Transaction);

    private static string Required(string value, string field, List<string> errors)
    {
        if (value.Length == 0) errors.Add($"{field} is required.");
        return value;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static decimal? DecimalValue(string value, string field, List<string> errors)
    {
        if (value.Length == 0) return null;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)) return parsed;
        errors.Add($"{field} is not a valid number.");
        return null;
    }

    private static int? IntegerValue(string value, string field, List<string> errors)
    {
        if (value.Length == 0) return null;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0) return parsed;
        errors.Add($"{field} must be a positive whole number.");
        return null;
    }

    private static bool YesNo(string value, string field, bool defaultValue, List<string> errors)
    {
        if (value.Length == 0) return defaultValue;
        if (string.Equals(value, "Y", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "YES", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(value, "N", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "NO", StringComparison.OrdinalIgnoreCase)) return false;
        errors.Add($"{field} must be Y or N.");
        return defaultValue;
    }

    private sealed record ImportLookups(
        IReadOnlyDictionary<string, long> Units,
        IReadOnlyDictionary<long, long> Taxes,
        IReadOnlyDictionary<string, string> Barcodes);
}
