using Dapper;
using GroceryErp.Core.Numerics;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Catalog.Barcodes;

public enum ScanResultKind { Empty, Item, Weight, Price, NotFound }

public sealed record ScanResult(
    ScanResultKind Kind,
    long? ProductId,
    long? LotId,
    Quantity? Quantity,
    Money? Price,
    string? Barcode)
{
    public static ScanResult Empty { get; } = new(ScanResultKind.Empty, null, null, null, null, null);
    public static ScanResult NotFound(string code) => new(ScanResultKind.NotFound, null, null, null, null, code);
}

public sealed class ScanResolver(DbExecutor database)
{
    public Task<ScanResult> ResolveAsync(string raw, CancellationToken cancellationToken = default)
    {
        string code = Normalize(raw);
        if (code.Length == 0) return Task.FromResult(ScanResult.Empty);
        return database.ReadAsync(connection =>
        {
            BarcodeHit? hit = FindBarcode(connection, code);
            if (hit is not null) return Item(hit, code);

            if (code.Length == 12) hit = FindBarcode(connection, "0" + code);
            else if (code.Length == 13 && code[0] == '0') hit = FindBarcode(connection, code[1..]);
            if (hit is not null) return Item(hit, code);

            foreach (RuleRow row in connection.Query<RuleRow>("""
                SELECT prefix AS Prefix, total_length AS TotalLength, item_start AS ItemStart,
                       item_length AS ItemLength, value_start AS ValueStart, value_length AS ValueLength,
                       value_kind AS ValueKind, value_scale AS ValueScale
                FROM barcode_rule WHERE is_active=1 ORDER BY length(prefix) DESC
                """))
            {
                var rule = new BarcodeRule(row.Prefix, row.TotalLength, row.ItemStart, row.ItemLength,
                    row.ValueStart, row.ValueLength,
                    row.ValueKind == "WEIGHT" ? BarcodeValueKind.Weight : BarcodeValueKind.Price,
                    row.ValueScale);
                if (!rule.TryParse(code, out string plu, out decimal value)) continue;
                long? productId = connection.QuerySingleOrDefault<long?>(
                    "SELECT id FROM product WHERE plu_code=@plu AND is_active=1", new { plu });
                if (productId is null) continue;
                return rule.Kind == BarcodeValueKind.Weight
                    ? new(ScanResultKind.Weight, productId, null, new Quantity(value), null, code)
                    : new(ScanResultKind.Price, productId, null, null, new Money(value), code);
            }

            if (Gs1.TryParse(code, out Gs1Data gs1) && gs1.Gtin is not null)
            {
                string ean = gs1.Gtin[0] == '0' ? gs1.Gtin[1..] : gs1.Gtin;
                hit = FindBarcode(connection, ean);
                if (hit is not null)
                {
                    long? lotId = hit.LotId ?? connection.QueryFirstOrDefault<long?>("""
                        SELECT id FROM product_lot
                        WHERE product_id=@ProductId
                          AND (@Batch IS NULL OR batch_no=@Batch)
                          AND (@Expiry IS NULL OR expiry_date=@Expiry)
                        ORDER BY CASE WHEN expiry_date IS NULL THEN 1 ELSE 0 END, expiry_date, received_date, id
                        LIMIT 1
                        """, new
                    {
                        hit.ProductId,
                        Batch = gs1.Batch,
                        Expiry = gs1.Expiry?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    });
                    return new(ScanResultKind.Item, hit.ProductId, lotId, Quantity.FromScaled(hit.PackQuantity), null, code);
                }
            }

            long? skuProduct = connection.QuerySingleOrDefault<long?>(
                "SELECT id FROM product WHERE sku=@code COLLATE NOCASE AND is_active=1", new { code });
            return skuProduct is null
                ? ScanResult.NotFound(code)
                : new(ScanResultKind.Item, skuProduct, null, Quantity.One, null, null);
        }, cancellationToken);
    }

    private static string Normalize(string? raw) => string.IsNullOrWhiteSpace(raw)
        ? string.Empty
        : raw.Trim().Normalize(System.Text.NormalizationForm.FormC);

    private static BarcodeHit? FindBarcode(System.Data.IDbConnection connection, string barcode) =>
        connection.QuerySingleOrDefault<BarcodeHit>("""
            SELECT pb.product_id AS ProductId, pb.lot_id AS LotId, pb.pack_qty AS PackQuantity
            FROM product_barcode pb JOIN product p ON p.id=pb.product_id
            WHERE pb.barcode=@barcode AND p.is_active=1
            """, new { barcode });

    private static ScanResult Item(BarcodeHit hit, string barcode) =>
        new(ScanResultKind.Item, hit.ProductId, hit.LotId, Quantity.FromScaled(hit.PackQuantity), null, barcode);

    private sealed class BarcodeHit
    {
        public long ProductId { get; init; }
        public long? LotId { get; init; }
        public long PackQuantity { get; init; }
    }

    private sealed class RuleRow
    {
        public string Prefix { get; init; } = string.Empty;
        public int TotalLength { get; init; }
        public int ItemStart { get; init; }
        public int ItemLength { get; init; }
        public int ValueStart { get; init; }
        public int ValueLength { get; init; }
        public string ValueKind { get; init; } = string.Empty;
        public long ValueScale { get; init; }
    }
}
