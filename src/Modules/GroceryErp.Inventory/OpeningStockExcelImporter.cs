using System.Globalization;
using ClosedXML.Excel;
using Dapper;
using GroceryErp.Core.Numerics;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Inventory;

public sealed record OpeningStockImportRow(int RowNumber, string Sku, OpeningStockLine? Line, IReadOnlyList<string> Errors)
{
    public bool IsValid => Line is not null && Errors.Count == 0;
}

public sealed record OpeningStockImportPreview(IReadOnlyList<OpeningStockImportRow> Rows)
{
    public int ValidCount => Rows.Count(static row => row.IsValid);
    public int ErrorCount => Rows.Count - ValidCount;
}

public sealed class OpeningStockExcelImporter(DbExecutor database, InventoryService inventory, IClock clock)
{
    private static readonly string[] Headers = ["SKU*", "Qty*", "Unit cost*", "Selling price*", "MRP", "Expiry", "Batch", "Received date"];

    public static void WriteTemplate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var workbook = new XLWorkbook();
        IXLWorksheet sheet = workbook.AddWorksheet("Opening Stock");
        for (int index = 0; index < Headers.Length; index++) sheet.Cell(1, index + 1).Value = Headers[index];
        sheet.Range(1, 1, 1, Headers.Length).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Column(1).Width = 20;
        sheet.Columns(2, 5).Width = 16;
        sheet.Columns(6, 8).Width = 18;
        sheet.Range("F2:F10000").Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Range("H2:H10000").Style.DateFormat.Format = "yyyy-mm-dd";
        workbook.SaveAs(path);
    }

    public Task<OpeningStockImportPreview> DryRunAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return database.ReadAsync(connection =>
        {
            using var workbook = new XLWorkbook(path);
            IXLWorksheet sheet = workbook.Worksheet(1);
            var rows = new List<OpeningStockImportRow>();
            int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int rowNumber = 2; rowNumber <= lastRow; rowNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sku = sheet.Cell(rowNumber, 1).GetString().Trim();
                if (string.IsNullOrWhiteSpace(sku) && sheet.Row(rowNumber).Cells(1, Headers.Length).All(static cell => cell.IsEmpty())) continue;
                var errors = new List<string>();
                ProductImportRule? product = connection.QuerySingleOrDefault<ProductImportRule>("""
                    SELECT p.id AS Id,p.track_expiry AS TrackExpiry,u.allow_fraction AS AllowFraction
                    FROM product p JOIN uom u ON u.id=p.uom_id WHERE p.sku=@sku AND p.is_active=1
                    """, new { sku });
                if (product is null) errors.Add("SKU was not found or is archived.");
                decimal? quantity = Decimal(sheet.Cell(rowNumber, 2), "Qty", errors);
                decimal? cost = Decimal(sheet.Cell(rowNumber, 3), "Unit cost", errors);
                decimal? price = Decimal(sheet.Cell(rowNumber, 4), "Selling price", errors);
                decimal? mrp = OptionalDecimal(sheet.Cell(rowNumber, 5), "MRP", errors);
                DateOnly? expiry = OptionalDate(sheet.Cell(rowNumber, 6), "Expiry", errors);
                string? batch = Optional(sheet.Cell(rowNumber, 7).GetString());
                DateOnly? received = OptionalDate(sheet.Cell(rowNumber, 8), "Received date", errors);
                received ??= clock.BusinessDate;
                if (quantity <= 0) errors.Add("Qty must be greater than zero.");
                if (cost < 0 || price < 0 || mrp < 0) errors.Add("Amounts cannot be negative.");
                if (mrp is not null && price > mrp) errors.Add("Selling price cannot exceed MRP.");
                if (product?.TrackExpiry == true && expiry is null) errors.Add("Expiry is required for this product.");
                if (product?.AllowFraction == false && quantity is not null && quantity != decimal.Truncate(quantity.Value))
                    errors.Add("This product requires a whole quantity.");
                if (expiry is not null && received is not null && expiry < received) errors.Add("Expiry cannot precede received date.");

                OpeningStockLine? line = product is not null && quantity is not null && cost is not null && price is not null && received is not null
                    ? new(product.Id, new Quantity(quantity.Value), new Money(cost.Value), new Money(price.Value),
                        mrp is null ? null : new Money(mrp.Value), expiry, batch, received.Value)
                    : null;
                rows.Add(new(rowNumber, sku, line, errors));
            }
            return new OpeningStockImportPreview(rows);
        }, cancellationToken);
    }

    public Task<long> ImportAsync(OpeningStockImportPreview preview, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        IReadOnlyList<OpeningStockLine> lines = preview.Rows.Where(static row => row.IsValid)
            .Select(static row => row.Line!).ToList();
        return inventory.ImportOpeningStockAsync(lines, operationId, cancellationToken);
    }

    private static decimal? Decimal(IXLCell cell, string field, List<string> errors)
    {
        if (cell.TryGetValue(out decimal value)) return value;
        errors.Add($"{field} is required and must be a number.");
        return null;
    }

    private static decimal? OptionalDecimal(IXLCell cell, string field, List<string> errors)
    {
        if (cell.IsEmpty()) return null;
        if (cell.TryGetValue(out decimal value)) return value;
        errors.Add($"{field} must be a number.");
        return null;
    }

    private static DateOnly? OptionalDate(IXLCell cell, string field, List<string> errors)
    {
        if (cell.IsEmpty()) return null;
        if (cell.TryGetValue(out DateTime date)) return DateOnly.FromDateTime(date);
        if (DateOnly.TryParseExact(cell.GetString().Trim(), ["yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed)) return parsed;
        errors.Add($"{field} must be a valid date.");
        return null;
    }

    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class ProductImportRule { public long Id { get; init; } public bool TrackExpiry { get; init; } public bool AllowFraction { get; init; } }
}
