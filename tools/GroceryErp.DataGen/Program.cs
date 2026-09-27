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
if (scale is <= 0m or > 1m) throw new ArgumentException("Scale must be > 0 and <= 1.", nameof(args));

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
var random = new Random(seed);
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
        connection.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        connection.Execute("UPDATE setting SET value='Phase 7 Performance Store' WHERE key='store.name';");
        InsertMasters(connection);
        InsertPurchasesAndLots(connection);
        InsertSales(connection);
        InsertOverheads(connection);
        connection.Execute("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA optimize;");

        string integrity = connection.ExecuteScalar<string>("PRAGMA integrity_check;") ?? "";
        long foreignKeys = connection.Query("PRAGMA foreign_key_check;").LongCount();
        long cacheMismatches = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM v_lot_cache_mismatch;");
        if (integrity != "ok" || foreignKeys != 0 || cacheMismatches != 0)
            throw new InvalidDataException($"Generated database failed verification: {integrity}, FK={foreignKeys}, cache={cacheMismatches}.");
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
            new { id = index, parent = index <= 10 ? (int?)null : 1 + index % Math.Min(10, categories), name = $"Category {index:D2}", local = index % 3 == 0 ? $"શ્રેણી {index}" : null }, transaction);
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
        connection.Execute("""
            INSERT INTO product(id,sku,name,name_local,search_terms,category_id,brand_id,uom_id,hsn_code,tax_rate_id,
              is_loose,plu_code,track_expiry,default_shelf_life_days,reorder_level,default_selling_price,default_mrp)
            VALUES (@id,@sku,@name,@local,@terms,@category,@brand,@uom,'1001',2,@loose,@plu,1,365,10000,@price,@mrp)
            """, new
        {
            id = index,
            sku = $"P{index:D6}",
            name = faker.Commerce.ProductName() + $" {index:D5}",
            local = index % 10 < 3 ? $"કરિયાણા વસ્તુ {index}" : null,
            terms = $"grocery item {index}",
            category = 1 + index % categories,
            brand = 1 + index % brands,
            uom = loose ? 2 : 1,
            loose = loose ? 1 : 0,
            plu = loose ? index.ToString("D5", CultureInfo.InvariantCulture) : null,
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
    int baseLines = lots / purchases;
    int extraLines = lots % purchases;
    for (int batchStart = 1; batchStart <= purchases; batchStart += 1_000)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        int batchEnd = Math.Min(purchases, batchStart + 999);
        for (int purchaseId = batchStart; purchaseId <= batchEnd; purchaseId++)
        {
            DateOnly received = start.AddDays(random.Next(end.DayNumber - start.DayNumber + 1));
            int lineCount = baseLines + (purchaseId <= extraLines ? 1 : 0);
            long total = lineCount * 10_000_000L;
            connection.Execute("""
                INSERT INTO purchase(id,doc_no,vendor_id,vendor_invoice_no,vendor_invoice_date,received_date,status,
                  subtotal,tax_total,grand_total,posted_at)
                VALUES (@id,@doc,@vendor,@invoice,@date,@date,'DRAFT',@subtotal,@tax,@grand,NULL)
                """, new
            {
                id = purchaseId,
                doc = $"GRN-{purchaseId:D6}",
                vendor = 1 + purchaseId % vendors,
                invoice = $"VI-{purchaseId:D7}",
                date = Iso(received),
                subtotal = total,
                tax = total / 20,
                grand = total + total / 20,
                at = Timestamp(received)
            }, transaction);
            for (int line = 1; line <= lineCount; line++, nextLot++)
            {
                int productId = 1 + (nextLot - 1) % products;
                long cost = 20_000 + productId % 180 * 1000L;
                long price = cost + Math.Max(5_000, cost / 4);
                DateOnly expiry = received.AddDays(90 + random.Next(641));
                connection.Execute("""
                    INSERT INTO purchase_line(id,purchase_id,line_no,product_id,batch_no,expiry_date,qty,unit_cost,
                      tax_rate_pct,tax_amount,line_total,landed_unit_cost,mrp,selling_price)
                    VALUES (@id,@purchase,@line,@product,@batch,@expiry,1000000,@cost,5000,500000,10500000,@cost,@mrp,@price);
                    INSERT INTO product_lot(id,product_id,lot_code,batch_no,purchase_line_id,vendor_id,received_date,
                      expiry_date,unit_cost,mrp,selling_price,qty_received)
                    VALUES (@id,@product,@lot,@batch,@id,@vendor,@received,@expiry,@cost,@mrp,@price,1000000);
                    INSERT INTO stock_movement(move_date,product_id,lot_id,type_code,qty,unit_cost,ref_type,ref_id)
                    VALUES (@received,@product,@id,'PURCHASE',1000000,@cost,'purchase_line',@id);
                    """, new
                {
                    id = nextLot,
                    purchase = purchaseId,
                    line,
                    product = productId,
                    batch = $"B{nextLot:D7}",
                    expiry = Iso(expiry),
                    cost,
                    mrp = price + 10_000,
                    price,
                    lot = $"L{nextLot:D7}",
                    vendor = 1 + purchaseId % vendors,
                    received = Iso(received)
                }, transaction);
            }
            connection.Execute("UPDATE purchase SET status='POSTED', posted_at=@at WHERE id=@id",
                new { id = purchaseId, at = Timestamp(received) }, transaction);
        }
        transaction.Commit();
        Console.WriteLine($"Purchases: {batchEnd:N0}/{purchases:N0}");
    }
}

void InsertSales(SqliteConnection connection)
{
    int daySpan = end.DayNumber - start.DayNumber + 1;
    long movementId = lots;
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
                int productId = 1 + (int)(Math.Pow(random.NextDouble(), 2) * products);
                int lotsForProduct = Math.Max(1, lots / products);
                int lotId = productId + products * random.Next(lotsForProduct);
                if (lotId > lots) lotId = productId;
                long qty = random.Next(1, 4) * 1000L;
                long unitCost = 20_000 + productId % 180 * 1000L;
                long unitPrice = unitCost + Math.Max(5_000, unitCost / 4);
                long lineGross = qty * unitPrice / 1000;
                long lineTaxable = lineGross * 1000 / 1050;
                long lineTax = lineGross - lineTaxable;
                long lineCost = qty * unitCost / 1000;
                gross += lineGross; taxable += lineTaxable; tax += lineTax; costTotal += lineCost;
                connection.Execute("""
                    INSERT INTO sale_line(sale_id,line_no,product_id,lot_id,product_name,qty,unit_price,mrp,tax_rate_pct,
                      taxable_amount,tax_amount,line_total,unit_cost,cost_amount,product_name_local,hsn_code,price_includes_tax)
                    VALUES (@sale,@line,@product,@lot,@name,@qty,@price,@mrp,5000,@taxable,@tax,@total,@cost,@costAmount,@local,'1001',1);
                    INSERT INTO stock_movement(id,move_date,product_id,lot_id,type_code,qty,unit_cost,ref_type,ref_id)
                    VALUES (@movement,@date,@product,@lot,'SALE',@moveQty,@cost,'sale_line',last_insert_rowid());
                    """, new
                {
                    sale = saleId,
                    line,
                    product = productId,
                    lot = lotId,
                    name = $"Product {productId:D5}",
                    qty,
                    price = unitPrice,
                    mrp = unitPrice + 10_000,
                    taxable = lineTaxable,
                    tax = lineTax,
                    total = lineGross,
                    cost = unitCost,
                    costAmount = lineCost,
                    local = productId % 10 < 3 ? $"કરિયાણા વસ્તુ {productId}" : null,
                    movement = ++movementId,
                    date = Iso(date),
                    moveQty = -qty
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
    transaction.Commit();
}

int Scaled(int value) => Math.Max(1, (int)Math.Round(value * scale, MidpointRounding.AwayFromZero));
static string Iso(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
static string Timestamp(DateOnly value) => $"{value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} 12:00:00";

