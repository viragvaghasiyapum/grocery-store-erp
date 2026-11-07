using System.Diagnostics;
using System.Globalization;
using Bogus;
using Dapper;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Data.Infrastructure;
using Microsoft.Data.Sqlite;

const int seed = 27092026;
Dictionary<string, string> options = args
    .Where(static value => value.StartsWith("--", StringComparison.Ordinal) && value.Contains('='))
    .Select(static value => value[2..].Split('=', 2))
    .ToDictionary(static pair => pair[0], static pair => pair[1], StringComparer.OrdinalIgnoreCase);
decimal scale = options.TryGetValue("scale", out string? rawScale) &&
                decimal.TryParse(rawScale, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsedScale)
    ? parsedScale : 1m;
if (scale is <= 0m or > 1.5m) throw new ArgumentException("Scale must be > 0 and <= 1.5.", nameof(args));

string requestedOutput = Path.GetFullPath(options.GetValueOrDefault("output",
    Path.Combine(Environment.CurrentDirectory, "artifacts", "data", "grocery-erp-5y.db")));
if (File.Exists(requestedOutput)) throw new IOException($"Output already exists: {requestedOutput}");
string generationRoot = Path.Combine(Path.GetTempPath(), $"grocery-erp-datagen-{Guid.NewGuid():N}");

int categories = Scaled(60);
int brands = Scaled(300);
int products = Scaled(20_000);
int vendors = Scaled(150);
int purchases = Scaled(26_000);
int lots = Math.Max(products, Scaled(150_000));
int bills = Scaled(640_000);
int expenses = Scaled(3_600);
int employees = Math.Max(3, Scaled(25));
int assets = Math.Max(2, Scaled(40));
var productNames = new string[products + 1];
var localNames = new string?[products + 1];
var lotPools = Enumerable.Range(0, products + 1).Select(static _ => new List<GeneratedLot>()).ToArray();
var random = new Random(seed);
int[] purchaseLineCounts = BuildPurchaseLineCounts();
Randomizer.Seed = new Random(seed);
var faker = new Faker("en_IND");
DateOnly end = new(2026, 9, 26);
DateOnly start = end.AddYears(-5).AddDays(1);
var timer = Stopwatch.StartNew();
var paths = new AppPaths(new SettingsBootstrap(generationRoot));
var factory = new SqliteConnectionFactory(paths);
new MigrationRunner(factory, NullMigrationBackup.Instance).Run();

try
{
    using (SqliteConnection connection = factory.Open())
    {
        // Generation is an offline bulk load. Give this connection a larger page cache
        // without increasing the interactive application's per-connection cache.
        connection.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-65536;");
        connection.Execute("UPDATE setting SET value='Phase 7 Performance Store' WHERE key='store.name';");
        InsertMasters(connection);
        InsertPurchasesAndLots(connection);
        InsertSales(connection);
        InsertOverheads(connection);
        connection.Execute("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA optimize;");

        string integrity = connection.ExecuteScalar<string>("PRAGMA integrity_check;") ?? "";
        long foreignKeys = connection.Query("PRAGMA foreign_key_check;").LongCount();
        long cacheMismatches = LotSourceReconciliation.CheckAll(connection, null).LongCount();
        long expiredSaleLines = connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM sale_line sl
            JOIN sale s ON s.id=sl.sale_id
            JOIN product_lot pl ON pl.id=sl.lot_id
            WHERE s.sale_date>pl.expiry_date
            """);
        int minPurchaseLines = connection.ExecuteScalar<int>(
            "SELECT MIN(lines) FROM (SELECT COUNT(*) AS lines FROM purchase_line GROUP BY purchase_id)");
        int maxPurchaseLines = connection.ExecuteScalar<int>(
            "SELECT MAX(lines) FROM (SELECT COUNT(*) AS lines FROM purchase_line GROUP BY purchase_id)");
        long paddedPlus = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM product WHERE is_loose=1 AND plu_code LIKE '0%'");
        // Every purchase adds up as the app's own purchases do: lines from quantity x rate, the header
        // from its lines.
        long purchaseMismatches = connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM purchase p JOIN (
              SELECT purchase_id,SUM(CAST(ROUND(qty*unit_cost/1000.0) AS INTEGER)) AS sub,SUM(discount) AS discount,
                     SUM(tax_amount) AS tax,SUM(line_total) AS total
              FROM purchase_line GROUP BY purchase_id) l ON l.purchase_id=p.id
            WHERE p.subtotal<>l.sub OR p.discount_total<>l.discount OR p.tax_total<>l.tax
               OR p.grand_total<>p.subtotal-p.discount_total+p.tax_total+p.other_charges+p.round_off
               OR l.total<>l.sub-l.discount+l.tax
            """);
        if (integrity != "ok" || foreignKeys != 0 || cacheMismatches != 0 || expiredSaleLines != 0 ||
            minPurchaseLines != 2 || maxPurchaseLines != 40 || paddedPlus != 0 || purchaseMismatches != 0)
            throw new InvalidDataException($"Generated database failed verification: {integrity}, FK={foreignKeys}, cache={cacheMismatches}, expired sale lines={expiredSaleLines}, purchases not adding up={purchaseMismatches}.");
    }

    SqliteConnection.ClearAllPools();
    Directory.CreateDirectory(Path.GetDirectoryName(requestedOutput)!);
    File.Move(paths.DatabaseFile, requestedOutput);
    Console.WriteLine($"Generated {requestedOutput}");
    Console.WriteLine($"Products={products:N0}, Lots={lots:N0}, Purchases={purchases:N0}, Bills={bills:N0}, Seed={seed}");
    Console.WriteLine($"Elapsed={timer.Elapsed:c}");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(generationRoot)) Directory.Delete(generationRoot, recursive: true);
}

void InsertMasters(SqliteConnection connection)
{
    using SqliteTransaction transaction = connection.BeginTransaction();
    for (int index = 1; index <= categories; index++)
        connection.Execute("INSERT INTO category(id,parent_id,name,name_local,expiry_alert_days,sort_order) VALUES (@id,@parent,@name,@local,30,@id)",
            new { id = index, parent = index <= 10 ? (int?)null : index <= 30 ? 1 + (index - 11) % 10 : 11 + (index - 31) % Math.Min(20, Math.Max(1, categories - 10)), name = $"Category {index:D2}", local = index % 3 == 0 ? $"શ્રેણી {index}" : null }, transaction);
    for (int index = 1; index <= brands; index++)
        connection.Execute("INSERT INTO brand(id,name) VALUES (@id,@name)", new { id = index, name = $"Brand {index:D3}" }, transaction);
    for (int index = 1; index <= vendors; index++)
        connection.Execute("INSERT INTO vendor(id,name,phone,state,payment_terms_days) VALUES (@id,@name,@phone,'Gujarat',@terms)",
            new { id = index, name = faker.Company.CompanyName() + $" {index}", phone = $"9{index:D9}", terms = index % 31 }, transaction);
    for (int index = 1; index <= products; index++)
    {
        bool loose = index % 20 == 0;
        long cost = 20_000 + index % 180 * 1000L;
        long price = cost + Math.Max(5_000, cost / 4);
        productNames[index] = faker.Commerce.ProductName() + $" {index:D5}";
        localNames[index] = index % 10 < 3 ? $"કરિયાણા વસ્તુ {index}" : null;
        connection.Execute("""
            INSERT INTO product(id,sku,name,name_local,search_terms,category_id,brand_id,uom_id,hsn_code,tax_rate_id,
              is_loose,plu_code,track_expiry,default_shelf_life_days,reorder_level,default_selling_price,default_mrp)
            VALUES (@id,@sku,@name,@local,@terms,@category,@brand,@uom,'1001',2,@loose,@plu,1,365,10000,@price,@mrp)
            """, new
        {
            id = index,
            sku = $"P{index:D6}",
            name = productNames[index],
            local = localNames[index],
            terms = $"grocery item {index}",
            category = 1 + index % categories,
            brand = 1 + index % brands,
            uom = loose ? 2 : 1,
            loose = loose ? 1 : 0,
            plu = loose ? index.ToString(CultureInfo.InvariantCulture) : null,
            price,
            mrp = price + 10_000,
        }, transaction);
        connection.Execute("INSERT INTO product_barcode(barcode,product_id,is_primary) VALUES (@barcode,@id,1)",
            new { barcode = Ean.Internal("21", index), id = index }, transaction);
    }
    transaction.Commit();
    Console.WriteLine($"Masters: {products:N0} products");
}

void InsertPurchasesAndLots(SqliteConnection connection)
{
    int nextLot = 1;
    for (int batchStart = 1; batchStart <= purchases; batchStart += 1_000)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        int batchEnd = Math.Min(purchases, batchStart + 999);
        for (int purchaseId = batchStart; purchaseId <= batchEnd; purchaseId++)
        {
            // Seed one lot per product before trading starts; later receipts follow Pareto demand.
            DateOnly received = nextLot <= products ? start : start.AddDays(random.Next(end.DayNumber - start.DayNumber + 1));
            int lineCount = purchaseLineCounts[purchaseId];
            // The header's totals are its lines' sums, set when the purchase is posted below.
            connection.Execute("""
                INSERT INTO purchase(id,doc_no,vendor_id,vendor_invoice_no,vendor_invoice_date,received_date,status,
                  subtotal,tax_total,grand_total,posted_at)
                VALUES (@id,@doc,@vendor,@invoice,@date,@date,'DRAFT',0,0,0,NULL)
                """, new
            {
                id = purchaseId,
                doc = $"GRN-{purchaseId:D6}",
                vendor = 1 + purchaseId % vendors,
                invoice = $"VI-{purchaseId:D7}",
                date = Iso(received),
            }, transaction);
            long subtotal = 0;
            long taxTotal = 0;
            for (int line = 1; line <= lineCount; line++, nextLot++)
            {
                int productId = nextLot <= products ? nextLot : PickProduct();
                long cost = 20_000 + productId % 180 * 1000L;
                long price = cost + Math.Max(5_000, cost / 4);
                DateOnly expiry = received.AddDays(90 + random.Next(641));
                // Every line is bought at GST 5% (tax rate 2) in Regular mode, so the booked cost is
                // the purchase price before tax and the lot keeps the line's tax per piece. A line is
                // 1,000 pieces, so its value before tax is 1,000 x the rate and its tax 5% of that.
                long lineTaxable = 1000L * cost;
                long lineTax = lineTaxable / 20;
                subtotal += lineTaxable;
                taxTotal += lineTax;
                connection.Execute("""
                    INSERT INTO purchase_line(id,purchase_id,line_no,product_id,batch_no,expiry_date,qty,unit_cost,
                      tax_rate_pct,tax_amount,line_total,landed_unit_cost,mrp,selling_price,
                      tax_rate_id,tax_label_snapshot,landed_unit_tax,landed_unit_cost_incl_tax)
                    VALUES (@id,@purchase,@line,@product,@batch,@expiry,1000000,@cost,5000,@lineTax,@lineTotal,@cost,@mrp,@price,
                      2,'GST 5%',@unitTax,@inclTax);
                    INSERT INTO product_lot(id,product_id,lot_code,batch_no,purchase_line_id,vendor_id,received_date,
                      expiry_date,unit_cost,mrp,selling_price,qty_received,
                      tax_rate_id_snapshot,tax_label_snapshot,tax_rate_pct_snapshot,unit_tax,unit_cost_incl_tax)
                    VALUES (@id,@product,@lot,@batch,@id,@vendor,@received,@expiry,@cost,@mrp,@price,1000000,
                      2,'GST 5%',5000,@unitTax,@inclTax);
                    UPDATE product_lot SET qty_on_hand=1000000 WHERE id=@id;
                    """, new
                {
                    id = nextLot,
                    purchase = purchaseId,
                    line,
                    product = productId,
                    batch = $"B{nextLot:D7}",
                    expiry = Iso(expiry),
                    cost,
                    lineTax,
                    lineTotal = lineTaxable + lineTax,
                    unitTax = UnitTax(cost),
                    inclTax = cost + UnitTax(cost),
                    mrp = price + 10_000,
                    price,
                    lot = $"L{nextLot:D7}",
                    vendor = 1 + purchaseId % vendors,
                    received = Iso(received)
                }, transaction);
                lotPools[productId].Add(new GeneratedLot(nextLot, received, expiry, 1_000_000));
            }
            connection.Execute("""
                UPDATE purchase SET status='POSTED',posted_at=@at,subtotal=@subtotal,tax_total=@taxTotal,
                  grand_total=@grandTotal WHERE id=@id
                """, new { id = purchaseId, at = Timestamp(received), subtotal, taxTotal, grandTotal = subtotal + taxTotal },
                transaction);
        }
        transaction.Commit();
        Console.WriteLine($"Purchases: {batchEnd:N0}/{purchases:N0}");
    }
    foreach (List<GeneratedLot> pool in lotPools)
        pool.Sort(static (left, right) => left.Received.CompareTo(right.Received));
}

int[] BuildPurchaseLineCounts()
{
    // Retain the exact lot target while covering small and 40-line supplier invoices.
    var counts = new int[purchases + 1];
    for (int id = 1; id <= purchases; id++) counts[id] = 2;
    int remaining = lots - purchases * 2;
    for (int id = Math.Min(1000, purchases); id <= purchases && remaining >= 38; id += 1000)
    {
        counts[id] = 40;
        remaining -= 38;
    }
    var spread = new Random(seed + 1);
    while (remaining > 0)
    {
        int id = spread.Next(1, purchases + 1);
        if (id % 7 == 0 || counts[id] == 40) continue;
        counts[id]++;
        remaining--;
    }
    return counts;
}

void InsertSales(SqliteConnection connection)
{
    int daySpan = end.DayNumber - start.DayNumber + 1;
    for (int batchStart = 1; batchStart <= bills; batchStart += 5_000)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        int batchEnd = Math.Min(bills, batchStart + 4_999);
        for (int saleId = batchStart; saleId <= batchEnd; saleId++)
        {
            DateOnly date;
            do { date = start.AddDays(random.Next(daySpan)); }
            while (date.Month is not (10 or 11) && random.Next(100) < 15);
            int lineCount = 2 + random.Next(9);
            connection.Execute("INSERT INTO sale(id,sale_date,status,created_at) VALUES (@id,@date,'DRAFT',@at)",
                new { id = saleId, date = Iso(date), at = Timestamp(date) }, transaction);
            long gross = 0, taxable = 0, tax = 0, costTotal = 0;
            for (int line = 1; line <= lineCount; line++)
            {
                long qty = random.Next(1, 4) * 1000L;
                int productId = 0;
                int lotId = 0;
                for (int attempt = 0; attempt < 1_000; attempt++)
                {
                    productId = PickProduct();
                    lotId = TakeAvailableLot(productId, date, qty);
                    if (lotId != 0) break;
                }
                if (lotId == 0) throw new InvalidDataException($"No stock is available for a sale on {date}.");
                long unitCost = 20_000 + productId % 180 * 1000L;
                long unitPrice = unitCost + Math.Max(5_000, unitCost / 4);
                long lineGross = qty * unitPrice / 1000;
                long lineTaxable = lineGross * 1000 / 1050;
                long lineTax = lineGross - lineTaxable;
                long lineCost = qty * unitCost / 1000;
                gross += lineGross; taxable += lineTaxable; tax += lineTax; costTotal += lineCost;
                connection.Execute("""
                    INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,unit_price,mrp,tax_rate_pct,
                      taxable_amount,tax_amount,line_total,unit_cost,cost_amount,product_name_local,hsn_code,price_includes_tax,
                      tax_label_snapshot,unit_cost_tax,unit_cost_incl_tax)
                    VALUES (@sale,@line,@product,@lot,@name,@qty,@price,@mrp,5000,@taxable,@tax,@total,@cost,@costAmount,@local,'1001',1,
                      'GST 5%',@unitTax,@inclTax);
                    UPDATE product_lot SET qty_on_hand=qty_on_hand-@qty WHERE id=@lot;
                    """, new
                {
                    sale = saleId,
                    line,
                    product = productId,
                    lot = lotId,
                    name = productNames[productId],
                    qty,
                    price = unitPrice,
                    mrp = unitPrice + 10_000,
                    taxable = lineTaxable,
                    tax = lineTax,
                    total = lineGross,
                    cost = unitCost,
                    unitTax = UnitTax(unitCost),
                    inclTax = unitCost + UnitTax(unitCost),
                    costAmount = lineCost,
                    local = localNames[productId],
                }, transaction);
            }
            connection.Execute("""
                UPDATE sale SET invoice_no=@invoice,status='COMPLETED',line_count=@lines,gross_total=@gross,
                  taxable_total=@taxable,tax_total=@tax,grand_total=@gross,cost_total=@cost,paid_total=@gross,completed_at=@at
                WHERE id=@id;
                INSERT INTO sale_payment(sale_id,mode,amount,paid_at) VALUES (@id,@mode,@gross,@at);
                """, new
            {
                id = saleId,
                invoice = $"INV-{saleId:D8}",
                lines = lineCount,
                gross,
                taxable,
                tax,
                cost = costTotal,
                at = Timestamp(date),
                mode = saleId % 3 == 0 ? "UPI" : "CASH"
            }, transaction);
        }
        transaction.Commit();
        Console.WriteLine($"Bills: {batchEnd:N0}/{bills:N0}");
    }
}

void InsertOverheads(SqliteConnection connection)
{
    using SqliteTransaction transaction = connection.BeginTransaction();
    for (int index = 1; index <= expenses; index++)
    {
        DateOnly date = start.AddDays(random.Next(end.DayNumber - start.DayNumber + 1));
        connection.Execute("INSERT INTO expense(expense_date,category_id,amount,mode,paid_to) VALUES (@date,@category,@amount,'CASH',@paid)",
            new { date = Iso(date), category = 1 + index % 10, amount = 5_000L + random.Next(500_000), paid = faker.Company.CompanyName() }, transaction);
    }
    for (int index = 1; index <= employees; index++)
    {
        connection.Execute("INSERT INTO employee(id,name,role,join_date,monthly_salary) VALUES (@id,@name,'Store staff',@date,@salary)",
            new { id = index, name = faker.Name.FullName(), date = Iso(start), salary = 12_000_000L + index * 250_000L }, transaction);
        for (DateOnly month = new(start.Year, start.Month, 1); month <= end; month = month.AddMonths(1))
            connection.Execute("INSERT INTO salary_payment(employee_id,period_month,pay_date,amount,mode) VALUES (@employee,@month,@date,@amount,'BANK')",
                new { employee = index, month = month.ToString("yyyy-MM", CultureInfo.InvariantCulture), date = Iso(month.AddMonths(1).AddDays(4)), amount = 12_000_000L + index * 250_000L }, transaction);
    }
    for (int index = 1; index <= assets; index++)
    {
        DateOnly date = start.AddDays(random.Next(Math.Max(1, end.DayNumber - start.DayNumber - 365)));
        connection.Execute("INSERT INTO asset(id,name,category,purchase_date,in_use_from,cost,vendor_id,method,rate_pct,salvage_value) VALUES (@id,@name,'Equipment',@date,@date,@cost,@vendor,@method,15000,@salvage)",
            new
            {
                id = index,
                name = $"Store equipment {index}",
                date = Iso(date),
                cost = 100_000_000L + index * 5_000_000L,
                vendor = 1 + index % vendors,
                method = index % 2 == 0 ? "SLM" : "WDV",
                salvage = 5_000_000L
            }, transaction);
    }
    // Every asset the app saves has its initial term (AssetService, and migration 34 for older ones);
    // without it, due depreciation fails in idle maintenance.
    connection.Execute("""
        INSERT INTO asset_term(asset_id,effective_month,cost,in_use_from,method,rate_pct,salvage_value,reason)
        SELECT id,substr(in_use_from,1,7),cost,in_use_from,method,rate_pct,salvage_value,'INITIAL' FROM asset
        """, transaction: transaction);
    transaction.Commit();
}

int PickProduct() => 1 + (int)(Math.Pow(random.NextDouble(), 2) * products);

int TakeAvailableLot(int productId, DateOnly saleDate, long quantity)
{
    List<GeneratedLot> pool = lotPools[productId];
    int low = 0, high = pool.Count;
    while (low < high)
    {
        int middle = low + (high - low) / 2;
        if (pool[middle].Received <= saleDate) low = middle + 1;
        else high = middle;
    }
    int eligible = low;
    for (int attempt = 0; attempt < Math.Min(8, eligible); attempt++)
    {
        GeneratedLot lot = pool[random.Next(eligible)];
        if (lot.Remaining < quantity || lot.Expiry < saleDate) continue;
        lot.Remaining -= quantity;
        return lot.Id;
    }
    for (int index = 0; index < eligible; index++)
    {
        GeneratedLot lot = pool[index];
        if (lot.Remaining < quantity || lot.Expiry < saleDate) continue;
        lot.Remaining -= quantity;
        return lot.Id;
    }
    return 0;
}

int Scaled(int value) => Math.Max(1, (int)Math.Round(value * scale, MidpointRounding.AwayFromZero));
static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
// One piece's GST 5% on a before-tax fx3 cost, rounded half up as Money.PercentOf rounds.
static long UnitTax(long cost) => (cost * 5000 + 50_000) / 100_000;
static string Timestamp(DateOnly value) => $"{value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} 12:00:00";

sealed class GeneratedLot(int id, DateOnly received, DateOnly expiry, long remaining)
{
    public int Id { get; } = id;
    public DateOnly Received { get; } = received;
    public DateOnly Expiry { get; } = expiry;
    public long Remaining { get; set; } = remaining;
}

