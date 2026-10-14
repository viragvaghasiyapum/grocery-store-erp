using System.Globalization;
using System.IO;
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
    private const int MaxImportRows = 50_000;
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

    public async Task<OpeningStockImportPreview> DryRunAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // ClosedXML parsing is CPU/memory work. Do it without holding a SQLite
        // reader, then resolve distinct SKUs in bounded batches.
        List<ParsedRow> parsed = await Task.Run(() => ParseWorkbook(path, cancellationToken), cancellationToken);
        string[] skus = parsed.Select(static row => row.Sku).Where(static sku => sku.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var products = await database.ReadAsync(connection =>
        {
            var found = new Dictionary<string, ProductImportRule>(StringComparer.OrdinalIgnoreCase);
            foreach (string[] batch in skus.Chunk(400))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (ProductImportRule product in connection.Query<ProductImportRule>("""
                    SELECT p.sku AS Sku,p.id AS Id,p.track_expiry AS TrackExpiry,
                           u.allow_fraction AS AllowFraction
                    FROM product p JOIN uom u ON u.id=p.uom_id
                    WHERE p.sku IN @batch AND p.is_active=1
                    """, new { batch }))
                    found[product.Sku] = product;
            }
            return found;
        }, cancellationToken);
        var rows = new List<OpeningStockImportRow>(parsed.Count);
        foreach (ParsedRow row in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var errors = row.Errors;
            products.TryGetValue(row.Sku, out ProductImportRule? product);
            if (product is null) errors.Add("SKU was not found or is archived.");
            if (product?.TrackExpiry == true && row.Expiry is null) errors.Add("Expiry is required for this product.");
            if (product?.AllowFraction == false && row.Quantity is not null && row.Quantity != decimal.Truncate(row.Quantity.Value))
                errors.Add("This product requires a whole quantity.");
            OpeningStockLine? line = product is not null && row.Quantity is not null && row.Cost is not null &&
                row.Price is not null && row.Received is not null
                ? new(product.Id, new Quantity(row.Quantity.Value), new Money(row.Cost.Value),
                    new Money(row.Price.Value), row.Mrp is null ? null : new Money(row.Mrp.Value),
                    row.Expiry, row.Batch, row.Received.Value)
                : null;
            rows.Add(new(row.RowNumber, row.Sku, line, errors));
        }
        return new OpeningStockImportPreview(rows);
    }

    private List<ParsedRow> ParseWorkbook(string path, CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook(path);
        IXLWorksheet sheet = workbook.Worksheet(1);
        int lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        if (lastRow - 1 > MaxImportRows)
            throw new InvalidDataException($"Opening-stock workbook exceeds {MaxImportRows:N0} rows. Split it into smaller files.");
        var rows = new List<ParsedRow>(Math.Max(0, lastRow - 1));
        for (int rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sku = sheet.Cell(rowNumber, 1).GetString().Trim();
            if (sku.Length == 0 && sheet.Row(rowNumber).Cells(1, Headers.Length).All(static cell => cell.IsEmpty())) continue;
            var errors = new List<string>();
            decimal? quantity = Decimal(sheet.Cell(rowNumber, 2), "Qty", errors);
            decimal? cost = Decimal(sheet.Cell(rowNumber, 3), "Unit cost", errors);
            decimal? price = Decimal(sheet.Cell(rowNumber, 4), "Selling price", errors);
            decimal? mrp = OptionalDecimal(sheet.Cell(rowNumber, 5), "MRP", errors);
            DateOnly? expiry = OptionalDate(sheet.Cell(rowNumber, 6), "Expiry", errors);
            string? batch = Optional(sheet.Cell(rowNumber, 7).GetString());
            DateOnly? received = OptionalDate(sheet.Cell(rowNumber, 8), "Received date", errors) ?? clock.BusinessDate;
            if (quantity <= 0) errors.Add("Qty must be greater than zero.");
            if (cost < 0 || price < 0 || mrp < 0) errors.Add("Amounts cannot be negative.");
            if (mrp is not null && price > mrp) errors.Add("Selling price cannot exceed MRP.");
            if (expiry is not null && received is not null && expiry < received) errors.Add("Expiry cannot precede received date.");
            rows.Add(new(rowNumber, sku, quantity, cost, price, mrp, expiry, batch, received, errors));
        }
        return rows;
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

    private sealed record ParsedRow(int RowNumber, string Sku, decimal? Quantity, decimal? Cost,
        decimal? Price, decimal? Mrp, DateOnly? Expiry, string? Batch, DateOnly? Received, List<string> Errors);
    private sealed class ProductImportRule { public string Sku { get; init; } = ""; public long Id { get; init; } public bool TrackExpiry { get; init; } public bool AllowFraction { get; init; } }
}
