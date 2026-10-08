# Implementation Guide — Grocery ERP

> How to build the Grocery ERP described in `techstack.md` on top of `grocery_erp_schema.sql`
> (migration 001), `002_i18n_pricing_reporting.sql` (migration 002), `003_operations.sql`
> (migration 003) and `004_stock_in.sql` (migration 004).
> Audience: the developer. Read §1–§5 before writing code; §6–§16 are per-module specs;
> §17–§21 cover quality, packaging and the delivery plan.

## Contents

1. [Scope & principles](#1-scope--principles)
2. [Solution structure](#2-solution-structure)
3. [Foundations](#3-foundations)
4. [UI architecture](#4-ui-architecture)
5. [Internationalization (i18n)](#5-internationalization-i18n)
6. [Authentication & first-run setup](#6-authentication--first-run-setup)
7. [Catalog](#7-catalog)
8. [Barcodes & scanning](#8-barcodes--scanning)
9. [Vendors & purchasing](#9-vendors--purchasing)
10. [Inventory](#10-inventory)
11. [Sales / POS](#11-sales--pos)
12. [Damages, expenses & salary](#12-damages-expenses--salary)
13. [Assets & depreciation](#13-assets--depreciation)
14. [Reports, dashboard & expiry area](#14-reports-dashboard--expiry-area)
15. [Printing](#15-printing)
16. [Backup, restore & integrity](#16-backup-restore--integrity)
17. [Performance engineering](#17-performance-engineering)
18. [Testing strategy](#18-testing-strategy)
19. [Packaging, installation & updates](#19-packaging-installation--updates)
20. [Store go-live checklist](#20-store-go-live-checklist)
21. [Delivery plan](#21-delivery-plan)
- Appendices: [A Keyboard map](#appendix-a--keyboard-map) · [B Error codes](#appendix-b--error-codes) · [C Settings keys](#appendix-c--settings-keys) · [D Glossary](#appendix-d--glossary) · [E Coding conventions](#appendix-e--coding-conventions)

---

## 1. Scope & principles

### 1.1 In scope

Product master with bilingual names, categories, brands, units, tax rates and barcodes · vendors
and purchases (goods receipt) that create **lots** · lot-level stock with FEFO · POS billing with
barcode scanning, discounts, GST, split payments, hold/resume, void, returns, receipt printing ·
damages and expiry write-offs · expenses · employees and salary · assets with yearly depreciation
· P&L (monthly, yearly, custom dates) · dashboard · expiry notification area · reports and Excel
export · English (default) and Gujarati UI, with all numbers always in English digits · backups
and restore.

### 1.2 Out of scope (v1)

Networking of any kind, multi-user/roles, multi-store, cloud sync, online payments integration,
GST return filing or e-invoicing (the app produces summaries for the accountant, not filings),
customer credit ledger (backlog — see §21.3), purchase orders, loyalty.

### 1.3 Engineering principles

1. **Two lines of defense.** Services validate first and produce friendly, localized messages;
   the database (CHECKs, FKs, triggers) is the last line and must never be relied on for UX.
2. **Money is never `double`.** `decimal` in code, `INTEGER × 1000` in SQLite, formatting only in UI.
3. **Stock only changes through the ledger** (`stock_movement`, append-only). Nothing else writes
   `product_lot.qty_on_hand`.
4. **Finalized documents are immutable.** Corrections are new documents (void, return, reversal).
5. **One user action = one transaction.** Completing a bill, posting a purchase, running a stock
   count: all-or-nothing.
6. **The UI thread never touches the database.**
7. **Every user-visible string comes from resources from day one** — even before translations exist.
8. **Keyboard first.** Every frequent action has a shortcut; the POS is fully usable without a mouse.
9. **Offline means the app owns reliability:** backups, integrity checks, clock sanity, recovery.
10. **Snapshots over joins for history.** Bills and movements copy names, prices, rates and costs.

---

## 2. Solution structure

### 2.1 Layout

```
GroceryErp.sln
Directory.Build.props            shared build settings
Directory.Packages.props         central package versions
.editorconfig
src/
  GroceryErp.App/                WPF host: Program.cs, App.xaml, Shell, composition root, themes
  GroceryErp.Core/               net10.0, BCL only — Money/Quantity/Percent, clock, fiscal year,
                                 errors, EAN/GS1 algorithms, pricing & depreciation math (pure, no IO)
  GroceryErp.Data/               SQLite connection factory, DbExecutor, migrations (embedded SQL),
                                 Dapper type handlers, doc numbering, settings store, audit
  GroceryErp.Localization/       TranslationSource, Tr markup extension, formatters, shared strings
  GroceryErp.UI.Common/          controls, behaviors, converters, dialogs, scan detector,
                                 module contract (IModule, NavItem)
  GroceryErp.Printing/           ESC/POS, TSPL, raster rendering, A4 documents, raw spooler
  Modules/
    GroceryErp.Catalog/          products, categories, brands, units, tax rates, barcodes, import
    GroceryErp.Purchasing/       vendors, purchases (GRN), vendor payments
    GroceryErp.Inventory/        lots, stock ledger, FEFO, stock count, expiry actions
    GroceryErp.Sales/            POS, invoices, payments, returns, day-end
    GroceryErp.Expenses/         expenses, damages, employees, salary
    GroceryErp.Assets/           asset register, depreciation posting
    GroceryErp.Reports/          P&L, dashboard, reports, exports
    GroceryErp.Settings/         auth, first-run wizard, settings UI, backup/restore UI
tests/
  GroceryErp.Core.Tests/         pure unit + property tests
  GroceryErp.Integration.Tests/  real schema on temp SQLite files
  GroceryErp.Architecture.Tests/ module-boundary rules
  GroceryErp.UI.Tests/           FlaUI smoke tests
tools/
  GroceryErp.DataGen/            synthetic 5-year dataset (Bogus)
  GroceryErp.Benchmarks/         BenchmarkDotNet
db/
  001_initial.sql                = grocery_erp_schema.sql (minus the final PRAGMA user_version)
  002_i18n_pricing_reporting.sql
  003_operations.sql             duplicate-submission protection (§3.15)
```

The `db/*.sql` files are linked into `GroceryErp.Data` as embedded resources.

### 2.2 Shared build settings

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <InvariantGlobalization>false</InvariantGlobalization>
    <!-- keep only our languages' satellite assemblies from NuGet packages -->
    <SatelliteResourceLanguages>en;gu</SatelliteResourceLanguages>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
```

`GroceryErp.Core.csproj` overrides `<TargetFramework>net10.0</TargetFramework>` (no WPF).
UI projects add `<UseWPF>true</UseWPF>`.

### 2.3 Module anatomy

Every module project has the same shape:

```
GroceryErp.Sales/
  Contracts/        interfaces + DTOs other modules may use (ISalesQueries, SaleSummaryDto)
  Domain/           pure logic: Cart, PricingEngine usage, rules (no IO)
  Application/      services: SaleService, ReturnService (transactions, validation, events)
  Infrastructure/   repositories: SaleRepository (Dapper SQL)
  Presentation/
    ViewModels/
    Views/
  Resources/        Strings.resx (English, neutral), Strings.gu.resx (Gujarati)
  SalesModule.cs    IModule implementation
```

### 2.4 Dependency rules

| Project | May reference |
|---|---|
| Core | **nothing** — .NET base class library only; no project references, no runtime packages (build-time analyzers are allowed) |
| Data, Localization | Core |
| Printing | Core, Localization |
| UI.Common | Core, Localization, `Microsoft.Extensions.DependencyInjection.Abstractions` |
| Module X | Core, Data, Localization, UI.Common, Printing, `Microsoft.Extensions.DependencyInjection.Abstractions`, **other modules' `Contracts` namespace only** |
| App | everything (composition root) |

**Dependency injection stays at the edges.** Only `UI.Common` (the `IModule` contract), the modules
and `App` know about `IServiceCollection`. Core, Data, Localization and Printing are plain classes
that declare what they need in their constructors; `App` registers them in
`AddFoundation()` (`GroceryErp.App/Composition/FoundationServices.cs`). Both rules are checked by
architecture tests (§18.9).

**Where the foundation types live**

| Project | Types |
|---|---|
| Core | `Fx3`, `Money`, `Quantity`, `Percent`, `MoneyMath`, `Tax`, `PricingEngine`, `LandedCost`, `Depreciation`, `Ean`, `Gs1`, `InputNormalizer`, `IClock`, `SystemClock`, `FiscalYear`, `ISettings`, `AppSettings`, `PeriodGuard`, `ErrorCode`, `DomainException`, messenger message records |
| Data | `AppPaths`, `SqliteConnectionFactory`, `DbTx`, `DbExecutor`, Dapper type handlers, `MigrationRunner`, `SqliteErrorMapper`, `DocNumberService`, `SettingsService`, `AuditLog`, `OperationGuard`, `BackupService` |
| Localization | `TranslationSource`, `TrExtension`, `DisplayFormatter`, `LocalizedText`, `Pseudo`, shared strings |
| UI.Common | `IModule`, `NavItem`, `PageViewModel`, `LocalizedViewModel`, `DecimalBox`, `ScanBurstDetector`, dialogs, converters, themes |
| Printing | `RawPrinter`, `EscPos`, TSPL builder, raster rendering, A4 documents, print queue |
| App | `Program`, `App`, `ShellWindow`, `AddFoundation`, `StartupPipeline`, `ShutdownPipeline`, `SingleInstance` |

Rule 4 is enforced by an architecture test (§18.9): no type in `GroceryErp.Sales` may depend on
`GroceryErp.Inventory.Application`/`Infrastructure`/`Presentation` — only on
`GroceryErp.Inventory.Contracts`.

Cross-module **writes** go through contracts. The most important one:

```csharp
namespace GroceryErp.Inventory.Contracts;

/// The ONLY way any module changes stock.
public interface IStockLedger
{
    void Post(DbTx tx, StockMovementInput movement);

    IReadOnlyList<LotAllocation> AllocateFefo(
        DbTx tx, long productId, Quantity requested, DateOnly asOf,
        IReadOnlyDictionary<long, Quantity> alreadyReserved);
}
```

### 2.5 Module registration

The module contract lives in `UI.Common`, not Core: it needs `IServiceCollection`, and its
navigation items point at WPF pages. Every module and `App` already reference `UI.Common`.

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace GroceryErp.UI.Common.Modularity;

public interface IModule
{
    /// Also the resource-key prefix for this module's strings ("Sales" → "Sales_Pos_Title").
    string Key { get; }
    System.Resources.ResourceManager Strings { get; }
    void Register(IServiceCollection services);
    IEnumerable<NavItem> Navigation { get; }
}

public sealed record NavItem(
    string TitleKey,          // resource key
    string Icon,              // Fluent symbol name, e.g. "ShoppingBag24"
    Type PageType,            // resolved from DI
    int Order,
    string? BadgeProvider = null);
```

Composition root (explicit list — no reflection scanning):

```csharp
// GroceryErp.App/App.xaml.cs
public partial class App : Application
{
    private static readonly IModule[] Modules =
    [
        new CatalogModule(), new PurchasingModule(), new InventoryModule(), new SalesModule(),
        new ExpensesModule(), new AssetsModule(), new ReportsModule(), new SettingsModule(),
    ];

    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (!SingleInstance.TryAcquire("GroceryErp.SingleInstance"))
            {
                SingleInstance.ActivateExisting();
                Shutdown();
                return;
            }

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSerilog((_, lc) => LoggingSetup.Configure(lc));
            builder.Services.AddFoundation();            // App-owned: registers Core/Data/Localization/Printing types

            TranslationSource.Instance.RegisterShared(SharedStrings.ResourceManager);
            foreach (var module in Modules)
            {
                module.Register(builder.Services);
                TranslationSource.Instance.Register(module.Key, module.Strings);
            }
            builder.Services.AddSingleton<IReadOnlyList<IModule>>(Modules);
            builder.Services.AddSingleton<ShellWindow>();

            _host = builder.Build();
            await _host.StartAsync();
            await _host.Services.GetRequiredService<StartupPipeline>().RunAsync();
        }
        catch (Exception ex)
        {
            FatalErrorDialog.Show(ex);   // logs, shows message, offers "open logs folder"
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.Services.GetRequiredService<ShutdownPipeline>().RunAsync(); // backup, checkpoint
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
```

---

## 3. Foundations

### 3.1 Paths

```csharp
public sealed class AppPaths
{
    public AppPaths(ISettingsBootstrap boot)
    {
        Root = boot.DataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GroceryErpData");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabaseFile)!);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ExportsDir);
    }

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "db", "grocery.db");
    public string LogsDir      => Path.Combine(Root, "logs");
    public string ExportsDir   => Path.Combine(Root, "exports");
    public string TempDir      => Path.Combine(Root, "temp");      // same volume as the DB: atomic moves
    public string DefaultBackupDir => Path.Combine(Root, "backups");
}
```

Rules:

- **Never** inside the Velopack install folder (`%LocalAppData%\GroceryErp\`) — uninstall deletes it.
- **Never** under OneDrive/Documents/Desktop. At first run and in Settings, reject any path that
  starts with `Environment.GetEnvironmentVariable("OneDrive")`.
- The data root can be changed in Settings (moves the DB after a backup). A tiny bootstrap file
  `%LocalAppData%\GroceryErp.boot.json` stores a non-default root.

### 3.2 Connections & pragmas

```csharp
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(AppPaths paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource  = paths.DatabaseFile,
            Mode        = SqliteOpenMode.ReadWriteCreate,
            Cache       = SqliteCacheMode.Private,
            Pooling     = true,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("""
            PRAGMA synchronous  = FULL;
            PRAGMA busy_timeout = 5000;
            PRAGMA temp_store   = MEMORY;
            PRAGMA cache_size   = -64000;
            """);
        return c;
    }

    /// One-time, outside any transaction (journal_mode is persistent in the file).
    public void EnsureWal()
    {
        using var c = Open();
        var mode = c.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Could not enable WAL (got '{mode}').");
    }
}
```

### 3.3 DbExecutor — threading and write serialization

Microsoft.Data.Sqlite's `*Async` methods run synchronously, so async wrappers would still block the
caller. All DB access therefore goes through `DbExecutor`, which moves work to the thread pool and
serializes writes (SQLite allows one writer; the gate prevents `SQLITE_BUSY` between, e.g., the
depreciation job and a sale).

```csharp
public readonly record struct DbTx(SqliteConnection Connection, SqliteTransaction Transaction);

public sealed class DbExecutor(SqliteConnectionFactory factory)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public Task<T> ReadAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var c = factory.Open();
            return work(c);
        }, ct);

    public async Task<T> WriteAsync<T>(Func<DbTx, T> work, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                using var c = factory.Open();
                using var tx = c.BeginTransaction(deferred: false);   // BEGIN IMMEDIATE
                var result = work(new DbTx(c, tx));
                tx.Commit();
                return result;
            }, ct);
        }
        catch (SqliteException ex)
        {
            throw SqliteErrorMapper.Map(ex);                          // §3.7
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task WriteAsync(Action<DbTx> work, CancellationToken ct = default) =>
        WriteAsync(tx => { work(tx); return true; }, ct);
}
```

Rules:

- Repositories are **synchronous** and take `SqliteConnection` or `DbTx`.
- Application services expose `Task` methods built on `DbExecutor`.
- ViewModels `await` services; they never see connections.

### 3.4 Dapper mapping

Register once at startup:

```csharp
public static class DapperSetup
{
    public static void Register()
    {
        SqlMapper.AddTypeHandler(new MoneyHandler());
        SqlMapper.AddTypeHandler(new QuantityHandler());
        SqlMapper.AddTypeHandler(new PercentHandler());
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
    }
}

public sealed class MoneyHandler : SqlMapper.TypeHandler<Money>
{
    public override void SetValue(IDbDataParameter p, Money value)
    {
        p.DbType = DbType.Int64;
        p.Value = value.Scaled;
    }
    public override Money Parse(object value) => Money.FromScaled(Convert.ToInt64(value));
}

public sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter p, DateOnly value)
    {
        p.DbType = DbType.String;
        p.Value = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
    public override DateOnly Parse(object value) =>
        DateOnly.ParseExact((string)value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
// QuantityHandler / PercentHandler: identical to MoneyHandler.
```

DTO rules for query results:

- Use `sealed class` with `{ get; init; }` properties and a parameterless constructor.
  **Do not** use positional records for results that contain custom-handled types — Dapper's
  constructor matching compares raw column types (`Int64`) with parameter types (`Money`) and fails.
- Alias columns to property names in SQL (`selling_price AS SellingPrice`) rather than relying on
  `MatchNamesWithUnderscores` — explicit is easier to grep.
- An integration test round-trips every handler (including nullable columns) against a real DB.

### 3.5 Migrations

```csharp
public sealed class MigrationRunner(SqliteConnectionFactory factory, BackupService backup,
                                    ILogger<MigrationRunner> log)
{
    private sealed record Script(int Version, string Name, string Sql);

    public void Run()
    {
        factory.EnsureWal();
        var scripts = LoadEmbedded();                 // "db/001_initial.sql" → Version 1, ordered
        var latest  = scripts[^1].Version;

        using var c = factory.Open();
        var current = c.ExecuteScalar<long>("PRAGMA user_version;");

        if (current > latest)
            throw new DomainException(ErrorCode.DatabaseNewerThanApp, current, latest);
        if (current == latest) return;
        if (current > 0) backup.Create($"pre-migration-v{current}");

        foreach (var s in scripts.Where(s => s.Version > current))
        {
            log.LogInformation("Applying migration {Version} {Name}", s.Version, s.Name);
            using var tx = c.BeginTransaction(deferred: false);
            c.Execute(s.Sql, transaction: tx);                        // multi-statement OK
            c.Execute($"PRAGMA user_version = {s.Version};", transaction: tx);
            tx.Commit();
        }
        c.Execute("PRAGMA optimize;");
    }

    private static List<Script> LoadEmbedded() { /* read manifest resources, parse "NNN_name.sql" */ }
}
```

Rules:

- Migrations are **append-only**: never edit a shipped script; add a new one.
- `001_initial.sql` is the schema file without its trailing `PRAGMA user_version = 1;`
  (the runner sets it).
- SQLite `ALTER TABLE` is limited; for changes it can't express (drop a CHECK, change a column type),
  use the documented 12-step rebuild (create new table → copy → drop → rename → recreate indexes,
  triggers, views) inside the migration.
- Test: fresh DB → latest, and a v1 DB with data → latest preserving data (§18.7).

### 3.6 Numeric model

```csharp
namespace GroceryErp.Core.Numerics;

public static class Fx3
{
    public const int Decimals = 3;
    public const long Factor = 1000;

    /// Commercial rounding to 3 places, half away from zero: 2.0005 → 2.001, −2.0005 → −2.001.
    public static decimal Round(decimal value) =>
        decimal.Round(value, Decimals, MidpointRounding.AwayFromZero);

    public static long ToScaled(decimal value) => decimal.ToInt64(Round(value) * Factor);
    public static decimal FromScaled(long scaled) => scaled / (decimal)Factor;
}

public readonly record struct Money : IComparable<Money>
{
    public decimal Value { get; }
    public Money(decimal value) => Value = Fx3.Round(value);

    public static readonly Money Zero = default;
    public long Scaled => Fx3.ToScaled(Value);
    public static Money FromScaled(long scaled) => new(Fx3.FromScaled(scaled));

    public static Money operator +(Money a, Money b) => new(a.Value + b.Value);
    public static Money operator -(Money a, Money b) => new(a.Value - b.Value);
    public static Money operator -(Money a) => new(-a.Value);
    public static Money operator *(Money price, Quantity qty) => new(price.Value * qty.Value);
    public static Money operator *(Quantity qty, Money price) => new(price.Value * qty.Value);

    public Money PercentOf(Percent p) => new(Value * p.Value / 100m);
    public Money DivideBy(Quantity q) =>
        q.Value == 0 ? throw new DivideByZeroException() : new(Value / q.Value);

    public static bool operator <(Money a, Money b)  => a.Value <  b.Value;
    public static bool operator >(Money a, Money b)  => a.Value >  b.Value;
    public static bool operator <=(Money a, Money b) => a.Value <= b.Value;
    public static bool operator >=(Money a, Money b) => a.Value >= b.Value;
    public int CompareTo(Money other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("0.000", CultureInfo.InvariantCulture);
}

// Quantity and Percent follow the same pattern.
// Quantity adds:  public bool IsWhole => Value == decimal.Truncate(Value);
//                 public static readonly Quantity One = new(1m);
// Percent is stored as the percentage number: 18% → Value 18.000 → Scaled 18000.
```

**Arithmetic rules**

| Operation | Rule |
|---|---|
| `qty × price` | one rounding: `Round(qty × price)` |
| Tax-inclusive split | `taxable = Round(gross × 100 / (100 + rate))`, `tax = gross − taxable` |
| Tax-exclusive | `tax = Round(taxable × rate / 100)`, `total = taxable + tax` |
| CGST/SGST | `cgst = Round(tax / 2)`, `sgst = tax − cgst` |
| Distribute an amount over lines | `MoneyMath.Allocate` (largest remainder) — Σ parts = total exactly |
| Per-unit from total | `Round(total / qty)` — only for costs, never for amounts that must sum |
| Bill round-off | `payable = Round(total / step) × step` (0 decimals), `round_off = payable − total` |

The "compute one part, derive the other by subtraction" rule guarantees every document's parts add
up to its total to the last 0.001, so the DB checks `line_total = taxable_amount + tax_amount` and
`grand_total = taxable_total + tax_total + round_off` always hold.

```csharp
public static class MoneyMath
{
    /// Splits a non-negative total across lines proportionally to weights, to 0.001,
    /// with Σ result == total exactly (largest-remainder method; ties → earlier line).
    public static IReadOnlyList<Money> Allocate(Money total, IReadOnlyList<Money> weights)
    {
        if (total < Money.Zero) throw new ArgumentOutOfRangeException(nameof(total));
        long totalMilli = total.Scaled;
        long weightSum = weights.Sum(w => w.Scaled);
        var shares = new long[weights.Count];
        if (weightSum <= 0 || totalMilli == 0) return shares.Select(Money.FromScaled).ToList();

        var remainders = new (int Index, decimal Rem)[weights.Count];
        long allocated = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            decimal exact = (decimal)totalMilli * weights[i].Scaled / weightSum;
            long floor = (long)decimal.Floor(exact);
            shares[i] = floor;
            remainders[i] = (i, exact - floor);
            allocated += floor;
        }
        long left = totalMilli - allocated;                      // 0 ≤ left < count
        foreach (var (index, _) in remainders.OrderByDescending(r => r.Rem)
                                             .ThenBy(r => r.Index).Take((int)left))
            shares[index]++;
        return shares.Select(Money.FromScaled).ToList();
    }

    public static Money RoundOff(Money total, Money step)
    {
        if (step <= Money.Zero) return Money.Zero;
        var payable = decimal.Round(total.Value / step.Value, 0, MidpointRounding.AwayFromZero) * step.Value;
        return new Money(payable - total.Value);
    }
}

public static class Tax
{
    public static (Money Taxable, Money Tax) FromInclusive(Money gross, Percent rate)
    {
        var taxable = new Money(gross.Value * 100m / (100m + rate.Value));
        return (taxable, gross - taxable);
    }

    public static (Money Taxable, Money Tax) FromExclusive(Money taxable, Percent rate) =>
        (taxable, taxable.PercentOf(rate));

    public static (Money Cgst, Money Sgst) SplitIntraState(Money tax)
    {
        var cgst = new Money(tax.Value / 2m);
        return (cgst, tax - cgst);
    }
}
```

**Worked example** (used again in tests, §18.4):

| Step | Value |
|---|---|
| Purchase 10 atta @ ₹250.000 ex-GST, 1 free, GST 5%, regular GST (input credit) | landed unit cost = 2,500 ÷ 11 = **₹227.273** |
| Sell 2 @ ₹290.000 incl. 5% GST | gross = **₹580.000** |
| Taxable = 580 × 100 ÷ 105 | **₹552.381** |
| Tax = 580.000 − 552.381 | **₹27.619** |
| COGS = 2 × 227.273 | **₹454.546** |
| Gross profit = 552.381 − 454.546 | **₹97.835** |

### 3.7 Errors

```csharp
public enum ErrorCode
{
    Unknown, NotFound, Validation,
    InsufficientStock, NoLotForProduct, ExpiredLot, FractionalQtyNotAllowed,
    SaleFinalized, PurchaseFinalized, LedgerAppendOnly, LotProductMismatch,
    PriceAboveMrp, DuplicateVendorBill, InvalidDate, PeriodLocked,
    BarcodeNotFound, BarcodeExists, InvalidBarcode,
    EmptyBill, PaymentShort, OverpaymentNonCash, CreditNeedsCustomer, DiscountExceedsLine,
    ReturnQtyExceedsSold, VoidNotAllowed, CancelNotAllowed,
    DatabaseNewerThanApp, ClockBehindLastBill, WrongPassword, BackupInvalid,
    // full list with messages: Appendix B
}

public sealed class DomainException(ErrorCode code, params object[] args)
    : Exception(code.ToString())
{
    public ErrorCode Code { get; } = code;
    public object[] Args { get; } = args;
}
```

**Mapping SQLite errors.** Trigger `RAISE(ABORT, …)` and CHECK/FK failures arrive as
`SqliteException` with `SqliteErrorCode == 19` (SQLITE_CONSTRAINT). `SqliteErrorMapper` matches the
message against a fixed table (the messages are defined in our own schema, so they are stable):

```csharp
public static class SqliteErrorMapper
{
    private static readonly (string Fragment, ErrorCode Code)[] Rules =
    [
        ("insufficient stock in lot",              ErrorCode.InsufficientStock),
        ("stock_movement is append-only",          ErrorCode.LedgerAppendOnly),
        ("sale is finalized",                      ErrorCode.SaleFinalized),
        ("completed sale is read-only",            ErrorCode.SaleFinalized),
        ("completed sales cannot be deleted",      ErrorCode.SaleFinalized),
        ("invalid sale status change",             ErrorCode.SaleFinalized),
        ("qty sign does not match movement type",  ErrorCode.Validation),
        ("purchase is posted/cancelled",           ErrorCode.PurchaseFinalized),
        ("selling_price <= mrp",                   ErrorCode.PriceAboveMrp),
        ("purchase.vendor_id, purchase.vendor_invoice_no", ErrorCode.DuplicateVendorBill),
        ("= date(",                                ErrorCode.InvalidDate),
        ("FOREIGN KEY constraint failed",          ErrorCode.Validation),
    ];

    public static Exception Map(SqliteException ex)
    {
        if (ex.SqliteErrorCode == 19)
            foreach (var (fragment, code) in Rules)
                if (ex.Message.Contains(fragment, StringComparison.Ordinal))
                    return new DomainException(code) { Data = { ["sqlite"] = ex.Message } };
        return ex;   // unknown → logged as a bug
    }
}
```

**Global handlers** (App startup): `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`,
`AppDomain.CurrentDomain.UnhandledException` → log with full context, show a localized dialog,
keep the app running where possible. Because bills are saved as drafts on every scan, even a crash
mid-bill loses nothing.

**ViewModel commands** use a single wrapper so every error is handled the same way:

```csharp
public abstract partial class PageViewModel : ObservableObject
{
    protected readonly IUserMessages Messages;         // localized Snackbar / dialog
    [ObservableProperty] private bool _isBusy;

    protected async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (DomainException ex) { Messages.Error(ex.Code, ex.Args); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Messages.Unexpected(ex); }
        finally { IsBusy = false; }
    }
}
```

### 3.8 Clock, business date & fiscal year

```csharp
public interface IClock
{
    DateTime Now { get; }                 // local time
    DateOnly BusinessDate { get; }        // shifted by business.day_start_hour
}

public sealed class SystemClock(ISettings settings) : IClock
{
    public DateTime Now => DateTime.Now;
    public DateOnly BusinessDate =>
        DateOnly.FromDateTime(DateTime.Now.AddHours(-settings.BusinessDayStartHour));
}

public static class FiscalYear
{
    public static int StartYear(DateOnly d, int startMonth) =>
        d.Month >= startMonth ? d.Year : d.Year - 1;

    /// "2026-27" for Apr-2026…Mar-2027 when startMonth = 4.
    public static string Label(DateOnly d, int startMonth)
    {
        var y = StartYear(d, startMonth);
        return startMonth == 1 ? $"{y}" : $"{y}-{(y + 1) % 100:D2}";
    }

    /// "2627" — used in document prefixes.
    public static string ShortCode(DateOnly d, int startMonth)
    {
        var y = StartYear(d, startMonth);
        return $"{y % 100:D2}{(y + 1) % 100:D2}";
    }

    public static (DateOnly From, DateOnly To) Range(int startYear, int startMonth)
    {
        var from = new DateOnly(startYear, startMonth, 1);
        return (from, from.AddYears(1).AddDays(-1));
    }

    public static int DaysIn(int startYear, int startMonth)
    {
        var (from, to) = Range(startYear, startMonth);
        return to.DayNumber - from.DayNumber + 1;
    }
}
```

**Clock sanity check (offline PCs drift).** At startup, if `today < MAX(sale.sale_date)` the app
shows a blocking warning ("The PC clock is behind your last bill of 22-09-2026 — fix the date
before billing") with an override that is written to `audit_log`. A dead CMOS battery otherwise
silently produces bills with wrong dates.

### 3.9 Document numbering

```csharp
public sealed class DocNumberService(ISettings settings)
{
    public string Next(DbTx t, DocType type, DateOnly date)
    {
        var fy = FiscalYear.Label(date, settings.FyStartMonth);
        var prefix = $"{type.Letter()}{FiscalYear.ShortCode(date, settings.FyStartMonth)}-"; // "S2627-"

        t.Connection.Execute("""
            INSERT INTO doc_sequence(doc_type, fiscal_year, prefix, next_no)
            VALUES (@type, @fy, @prefix, 1)
            ON CONFLICT(doc_type, fiscal_year) DO NOTHING
            """, new { type = type.ToString(), fy, prefix }, t.Transaction);

        var (p, no) = t.Connection.QuerySingle<(string, long)>("""
            UPDATE doc_sequence SET next_no = next_no + 1
            WHERE doc_type = @type AND fiscal_year = @fy
            RETURNING prefix, next_no - 1
            """, new { type = type.ToString(), fy }, t.Transaction);

        return $"{p}{no:D6}";                                  // S2627-000123 (12 chars ≤ 16)
    }
}
```

Numbers are taken **inside** the transaction that finalizes the document, so a rolled-back sale
never consumes a number → gapless series per financial year (GST requirement).

| DocType | Letter | Example |
|---|---|---|
| Sale | S | S2627-000123 |
| SaleReturn | R | R2627-000004 |
| Purchase (GRN) | P | P2627-000051 |
| Damage | D | D2627-000009 |
| Lot | L (fiscal year `*`) | L000123 |

### 3.10 Settings

- Stored as key/value rows in `setting`; read once into a typed, immutable `AppSettings` snapshot.
- `ISettings` exposes the current snapshot; `ISettingsService.SaveAsync(changes)` writes rows in
  one transaction, swaps the snapshot, and publishes `SettingsChangedMessage`.
- Every key has a default in code, so keys added in later versions work without a migration.
  Full list: Appendix C.

### 3.11 Period lock

`books.locked_until` (a date, empty = off). Every service that creates or changes a dated record
calls `PeriodGuard.EnsureOpen(date)` → `ErrorCode.PeriodLocked`. Use it after the accountant has
finalized a year so nobody back-dates entries into closed books.

### 3.12 Audit log

`IAuditLog.Write(DbTx t, string entity, long id, string action, object? changes)` serializes a
`{ "field": [old, new] }` JSON diff with `System.Text.Json`. Written for: product create/update/
archive, lot price changes, lot block/unblock, sale void, purchase cancel, stock adjustments,
settings changes, clock-warning overrides, restores.

### 3.13 Startup and shutdown pipelines

Startup (`StartupPipeline.RunAsync`):

1. Serilog up; log app version, OS build, data root.
2. Register Dapper handlers.
3. **Capability check:** `SELECT sqlite_version()` ≥ 3.37, and create/drop a temp
   `fts5(x, tokenize='trigram')` table. Fail fast with a clear message if missing.
4. Free disk space check (< 1 GB → warning).
5. **Migrations** (§3.5).
6. Load settings snapshot; apply culture & UI language (§5.4).
7. **Restore marker** present? → finish or undo the restore (§16.5).
   First run? → setup wizard (§6.1). Else → login (§6.2).
8. **Clock sanity** (§3.8).
9. Show shell. Then in the background:
   - lot-cache integrity check (`v_lot_cache_mismatch` must be empty — §10.8),
   - depreciation posting for elapsed months (§13.3),
   - daily backup if none today (§16.1),
   - refresh expiry/low-stock badges,
   - weekly `PRAGMA integrity_check` (if last run > 7 days),
   - purge `operation` rows older than 30 days (§3.15), leftover `*.partial` backups and old
     `temp/` folders.

Shutdown (`ShutdownPipeline.RunAsync`): backup (`reason = close`), `PRAGMA wal_checkpoint(TRUNCATE)`,
`PRAGMA optimize`, flush logs.

### 3.14 Cross-module notifications

`WeakReferenceMessenger.Default` messages (defined in `GroceryErp.Core.Messages`):

| Message | Published by | Typical subscribers |
|---|---|---|
| `SaleCompletedMessage(saleId)` | Sales | Dashboard, badges, popularity cache |
| `StockChangedMessage(productIds)` | Inventory ledger | Product list, lot views, badges |
| `SettingsChangedMessage(keys)` | Settings | Printing, formatters, shell |
| `LanguageChangedMessage(culture)` | Localization | Shell, open pages |
| `BackupCompletedMessage(info)` | Backup | Shell status indicator |

Messages carry IDs, never entities; subscribers re-query what they need.

### 3.15 Operation IDs — duplicate-submission protection

A double-click, a repeated Enter, or a retry after a slow response must never save the same
damage, expense, payment, return or bill twice. The UI's busy flag (§4.2) is the first defense;
the database is the guarantee (migration 003).

**Rule:** every command that finalizes something carries an `OpId` — 32 lowercase hex characters
(`Guid.NewGuid().ToString("N")`) generated by the ViewModel **when the form, dialog or bill opens**.
The service claims it first, inside the same transaction as the work:

```csharp
public enum OpType
{
    SaleComplete, SaleVoid, SaleReturn, PurchasePost, PurchaseCancel, VendorPayment,
    Damage, DamageBulk, Expense, SalaryRun, StockCountPost, StockAdjustment, PriceChange,
    AssetDispose, OpeningStockImport, ProductImport,
}

public sealed record OperationRef(string? RefType, long? RefId);

public static class OperationGuard
{
    /// Claims opId in the current transaction. Returns null the first time; otherwise the
    /// reference recorded by the original, already-committed operation.
    public static OperationRef? Claim(DbTx t, string opId, OpType type)
    {
        int inserted = t.Connection.Execute("""
            INSERT INTO operation(op_id, op_type) VALUES (@opId, @type)
            ON CONFLICT(op_id) DO NOTHING
            """, new { opId, type = type.ToCode() }, t.Transaction);       // SaleComplete → SALE_COMPLETE
        return inserted == 1 ? null : t.Connection.QuerySingle<OperationRef>(
            "SELECT ref_type AS RefType, ref_id AS RefId FROM operation WHERE op_id = @opId",
            new { opId }, t.Transaction);
    }

    public static void Complete(DbTx t, string opId, string refType, long refId) =>
        t.Connection.Execute(
            "UPDATE operation SET ref_type = @refType, ref_id = @refId WHERE op_id = @opId",
            new { opId, refType, refId }, t.Transaction);
}
```

Service pattern (identical for every covered command):

```csharp
public Task<SavedDamage> SaveAsync(SaveDamageCommand cmd) =>
    db.WriteAsync(t =>
    {
        if (OperationGuard.Claim(t, cmd.OpId, OpType.Damage) is { RefId: long doneId })
            return repo.LoadSaved(t, doneId) with { IsReplay = true };   // already saved: same result

        // … validation, damage row, DAMAGE movement …
        OperationGuard.Complete(t, cmd.OpId, "damage", damageId);
        return new SavedDamage(damageId, docNo);
    });
```

Why this is safe:

- The claim and the work commit or roll back **together**. A failed attempt (validation error,
  power cut) leaves no claim, so correcting the form and saving again with the same `OpId` works.
- Writes are serialized (`DbExecutor` gate + `BEGIN IMMEDIATE`), so a second submission waits for
  the first, then finds the claim and replays the original result instead of repeating it.
- Results carry `IsReplay`; the UI shows the normal success state but does **not** print, open the
  drawer or show a second toast.
- ViewModels create a new `OpId` only after a success ("Save & new", next bill); on failure they keep it.
- Rows are purged after 30 days at startup — they only matter for seconds. The business history
  lives in the documents and `audit_log`.

Covered commands (`op_type`): `SALE_COMPLETE`, `SALE_VOID`, `SALE_RETURN`, `PURCHASE_POST`,
`PURCHASE_CANCEL`, `VENDOR_PAYMENT`, `DAMAGE`, `DAMAGE_BULK`, `EXPENSE`, `SALARY_RUN`,
`STOCK_COUNT_POST`, `STOCK_ADJUSTMENT`, `PRICE_CHANGE`, `ASSET_DISPOSE`, `OPENING_STOCK_IMPORT`,
`PRODUCT_IMPORT`. Draft autosaves (POS lines, purchase drafts) are naturally idempotent updates
and don't need an `OpId`.

---

## 4. UI architecture

### 4.1 Shell

`ShellWindow` is a WPF UI `FluentWindow` with:

- **Title bar:** store name · business date · language switcher (EN / ગુ) · backup status dot
  (green = backed up today and USB export < 7 days; amber/red otherwise) · lock button.
- **Left `NavigationView`** (collapsible), items contributed by modules, ordered:
  Dashboard · Billing (POS) · Products · Stock · Purchases · Vendors · Sales history · Returns ·
  Damages · Expenses · Salary · Assets · Reports · Settings.
- **Badges** on Stock (expired + expiring ≤ 7 days) and Products (low stock).
- **Content frame** hosting pages resolved from DI.

Navigation contract: pages implement

```csharp
public interface IActivatable
{
    Task OnActivatedAsync(object? parameter, CancellationToken ct);  // load data here, never in ctor
    Task<bool> CanDeactivateAsync();                                  // unsaved-changes prompt
}
```

The shell cancels the previous page's `CancellationTokenSource` on navigation so slow queries
never update a page the user already left.

### 4.2 MVVM conventions

- One ViewModel per page/dialog; Views contain **no logic** beyond focus/keyboard plumbing.
- `[ObservableProperty]` for state, `[RelayCommand]` for actions (async commands get
  `CanExecute` and `IncludeCancelCommand = true` where useful).
- ViewModels depend on application services and `IUserMessages`, `INavigator`, `IDialogs`.
- Grid rows are lightweight row ViewModels or immutable DTOs; never live entities.
- ViewModels are **transient**; the POS ViewModel is **singleton** (the open bill survives
  navigating away and back).

### 4.3 Design system

| Token | Value |
|---|---|
| Base font size | 14 px (UI), 13 px (dense grids) |
| POS totals | 32–40 px, semibold |
| Spacing scale | 4 · 8 · 12 · 16 · 24 · 32 |
| Corner radius | 4 (inputs), 8 (cards/dialogs) |
| Minimum target | 36 px high (touch-friendly on touchscreen monitors) |
| Minimum resolution | 1366 × 768 — every screen must fit without horizontal scrolling |
| Theme | Light by default (bright shops, glare); Dark and System available |
| Status colors | Success/Warning/Danger always paired with an icon or text (never color alone) |
| Backdrop | None by default; Mica optional (`ui.backdrop`) |

Define tokens once in `GroceryErp.UI.Common/Themes/Tokens.xaml`; no hard-coded sizes or colors in pages.

### 4.4 Keyboard-first behavior

- Global shortcuts via `InputBindings` on the shell; page shortcuts on the page root.
- **F1** shows a shortcut overlay for the current page (generated from the same binding table).
- In forms, **Enter moves to the next field** (attached behavior); **Ctrl+S** saves;
  **Esc** cancels/closes.
- After any dialog closes on the POS, focus returns to the scan box.
- Full map: Appendix A.

### 4.5 Data grids

Virtualization only works if you keep it intact:

```xml
<DataGrid ItemsSource="{Binding Rows}"
          EnableRowVirtualization="True"
          EnableColumnVirtualization="True"
          VirtualizingPanel.IsVirtualizing="True"
          VirtualizingPanel.VirtualizationMode="Recycling"
          VirtualizingPanel.ScrollUnit="Item"
          AutoGenerateColumns="False"
          IsReadOnly="True"
          MinRowHeight="32" />
```

Rules:

- **Never** place a `DataGrid` inside a `ScrollViewer` or an auto-sized `StackPanel` — it gets
  infinite height and virtualizes nothing. Put it in a `Grid` row with `Height="*"`.
- Fixed or star column widths; no `Auto` widths on large lists.
- Row height must fit two lines (English name + local name) — never a fixed small height.
- Small masters (≤ 50k rows: products, vendors) load fully; histories (sales, movements) use
  **keyset pagination**, loading 200 rows at a time as the user scrolls:

```sql
SELECT id, invoice_no, sale_date, grand_total, status
FROM sale
WHERE status IN ('COMPLETED','VOID')
  AND (sale_date, id) < (@lastDate, @lastId)       -- row-value comparison
ORDER BY sale_date DESC, id DESC
LIMIT 200;
```

- Column widths and order persisted per grid in `ui.grid.<gridName>` settings.
- Ctrl+C copies selected rows as tab-separated text (pastes cleanly into Excel).

### 4.6 Forms & validation

- Form ViewModels derive from `ObservableValidator`; rules via attributes + custom
  `[CustomValidation]` methods; messages from resources (`Validation_*` keys).
- Save disabled while `HasErrors`; the first invalid field gets focus on save attempt.
- Dirty tracking: `CanDeactivateAsync` asks "Discard changes?" when navigating away.
- Every form or dialog that finalizes something holds an `OpId` created when it opens (§3.15) and
  passes it with the save command; a new one is created only after a successful save.
- Service-level validation repeats every rule (a ViewModel is not a security boundary).

### 4.7 Numeric input

A single `DecimalBox` control (or attached behavior on `TextBox`) used for every money, quantity
and percent field:

- Accepts digits and one `.`; rejects more than 3 decimals as you type. Gujarati digits (૦–૯)
  typed with a Gujarati layout are converted to `0–9` as you type — the box only ever shows English digits.
- Parses with `CultureInfo.InvariantCulture`; displays formatted (`1,23,456.789`) when not focused
  and raw (`123456.789`) when focused; selects all on focus.
- `AllowFraction` property — bound to the unit's `allow_fraction` for quantities.
- Up/Down keys increment by 1 (or by 0.001 with Ctrl).
- Binds to `decimal`; the ViewModel converts to `Money`/`Quantity`/`Percent`.
- `InputMethod.IsInputMethodEnabled="False"` and English input language (see §5.6).

### 4.8 Search box component

Used for product search on the POS, product list, purchase lines, damages:

- 150 ms debounce; each keystroke cancels the previous query (`CancellationTokenSource`).
- Runs on the thread pool via `DbExecutor.ReadAsync`; max 30 results.
- ↑/↓ to move, Enter to pick, Esc to clear; mouse optional.
- Shows name, local name, SKU, stock, price; highlights the matched fragment.
- Digit-only input of length 8/12/13/14 is treated as a barcode first (§8).

### 4.9 Dialogs & notifications

- `ContentDialog` for confirmations and small forms (payment, quantity, lot picker).
- `Snackbar` for success/info ("Bill S2627-000123 saved"); errors that need action use dialogs.
- Destructive actions (void, write-off, restore) need a typed reason or explicit confirmation.

### 4.10 Responsiveness rules

- Any operation that may exceed ~300 ms shows a progress indicator; > 2 s gets a cancel button.
- Page data loads in `OnActivatedAsync`, never in constructors.
- Heavy visuals (charts) load after the page's first render.

---

## 5. Internationalization (i18n)

### 5.1 Scope, languages and the numbers rule

| Layer | What | Mechanism |
|---|---|---|
| **UI text** | Labels, menus, messages, enum names, report headings, printed labels | `.resx` resources, runtime switch |
| **Numbers** | Money, quantities, weights, percentages, dates, times, counts, document numbers | **Always English digits `0–9`**, fixed `en-IN` formatting — never affected by the language |
| **Data** | Product/category/expense-category/unit names entered by the user | Bilingual columns: `name` (English) + `name_local` (Gujarati) |

Exactly two languages:

| Culture | Language | Role | Resource file |
|---|---|---|---|
| `en-IN` | English (India) | **Default** — first launch, fallback, logs | *(neutral)* `Strings.resx` |
| `gu-IN` | Gujarati (India) | Optional UI and print language | `Strings.gu.resx` |

- The app always starts in **English** on first launch, including the first-run wizard. The user
  can switch to Gujarati at any time (wizard step 1, title-bar switcher, Settings); the choice is
  saved in `ui.language` and survives restarts.
- The Gujarati resources use the neutral `gu` file, so `gu-IN` falls back to `gu`, then to English.
- `name_local` always holds the **Gujarati** name (UI label: "Gujarati name / ગુજરાતી નામ").

**The numbers rule.** Every number the app shows, prints, exports or accepts uses English digits
`0–9`, whichever language is selected: money, quantities, weights, percentages, GST rates, dates,
times, invoice/lot/document numbers, barcodes, SKUs, HSN codes, phone numbers, GSTIN, pincodes,
page numbers, counts inside Gujarati sentences ("3 વસ્તુઓ"), chart axes, receipts, labels, A4
documents, PDFs and Excel files. Gujarati digits (`૦–૯`, U+0AE6–U+0AEF) never appear in output and
are converted to `0–9` wherever they are typed. Enforcement is layered:

| Layer | Mechanism | Section |
|---|---|---|
| Formatting | Fixed `en-IN` format culture; UI culture is never used for numbers | §5.4 |
| Rendering | WPF `NumberSubstitution` pinned to European digits on every window and print visual | §5.5 |
| Input | `InputNormalizer` on every text input, numeric box converts while typing | §5.6, §4.7 |
| Data | Gujarati digits in user text converted to `0–9` on save | §5.7 |
| Resources | Translations may not contain Gujarati digits | §5.2, §18.8 |
| Tests | Automated checks on resources, formatter, receipts and UI smoke run | §18.8 |

### 5.2 Resource files and key naming

Each module owns `Resources/Strings.resx` (English) + `Strings.gu.resx` (Gujarati). Shared strings live in
`GroceryErp.Localization/Resources/Shared.resx`.

Key format: `{Prefix}_{Area}_{Item}` — letters, digits, underscores only.

| Prefix | Owner | Examples |
|---|---|---|
| `Common` | Shared | `Common_Save`, `Common_Cancel`, `Common_Search` |
| `Error` | Shared | `Error_InsufficientStock`, `Error_PeriodLocked` |
| `Validation` | Shared | `Validation_Required`, `Validation_MaxDecimals` |
| `Enum` | Shared | `Enum_SaleStatus_COMPLETED`, `Enum_PaymentMode_UPI` |
| `Print` | Shared | `Print_Receipt_Total`, `Print_Receipt_YouSaved` |
| `Pnl` | Shared | `Pnl_Head_SALES`, `Pnl_Head_COGS` |
| `Seed` | Settings | `Seed_ExpenseCategory_Rent`, `Seed_Uom_KG` |
| Module key | Module | `Catalog_Product_NameLocal`, `Sales_Pos_Title` |

Rules:

- Placeholders are numbered (`{0}`, `{1}`) and **every translation must contain the same set**.
- Use the `.resx` *Comment* column for context ("Button on POS payment dialog, max 12 chars").
- Never concatenate translated fragments into sentences — word order differs across languages.
- Never put user data inside resource strings except through placeholders.
- Numbers are never written into translations; they arrive through placeholders already formatted
  with English digits. A Gujarati digit (`૦–૯`) in a resource value fails the build's i18n tests.
  Key names like `F12` in shortcut hints stay in English digits too ("F12 દબાવો").

### 5.3 Runtime-switchable translations

```csharp
namespace GroceryErp.Localization;

public sealed class TranslationSource : INotifyPropertyChanged
{
    public static TranslationSource Instance { get; } = new();

    private readonly Dictionary<string, ResourceManager> _byPrefix = new(StringComparer.Ordinal);
    private CultureInfo _uiCulture = CultureInfo.GetCultureInfo("en-IN");

    /// Fixed: numbers and dates are always formatted en-IN (English digits). Not a setting.
    public static CultureInfo FormatCulture { get; } = CultureInfo.GetCultureInfo("en-IN");
    public bool PseudoLocalize { get; set; }                      // QA mode, §5.11
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Register(string prefix, ResourceManager manager) => _byPrefix[prefix] = manager;

    public void RegisterShared(ResourceManager shared)
    {
        foreach (var p in new[] { "Common", "Error", "Validation", "Enum", "Print", "Pnl", "Seed" })
            _byPrefix[p] = shared;
    }

    public string this[string key] => Get(key, _uiCulture);

    public string Get(string key, CultureInfo culture)
    {
        var sep = key.IndexOf('_');
        if (sep > 0 && _byPrefix.TryGetValue(key[..sep], out var rm) &&
            rm.GetString(key, culture) is { } value)
            return PseudoLocalize ? Pseudo.Transform(value) : value;

        MissingKeys.Report(key);             // logged once per key; tests fail on any
        return $"⟦{key}⟧";                   // visible in the UI, never blank
    }

    public string Format(string key, params object[] args) =>
        string.Format(FormatCulture, this[key], args);

    /// CLDR plural rules: en → one if n = 1; gu → one if integer part = 0 or n = 1.
    /// The number itself is formatted with FormatCulture (English digits) in both languages.
    public string Plural(string baseKey, decimal n)
    {
        bool one = _uiCulture.TwoLetterISOLanguageName == "gu"
            ? decimal.Truncate(n) == 0 || n == 1
            : n == 1;
        return Format($"{baseKey}_{(one ? "One" : "Other")}", n);
    }

    public CultureInfo UiCulture
    {
        get => _uiCulture;
        set
        {
            if (Equals(_uiCulture, value)) return;
            _uiCulture = value;
            CultureInfo.CurrentUICulture = value;
            CultureInfo.DefaultThreadCurrentUICulture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));   // all {l:Tr}
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UiCulture)));
        }
    }
}
```

Markup extension — binds to the indexer, so changing `UiCulture` refreshes every label instantly:

```csharp
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = TranslationSource.Instance,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}
```

Usage:

```xml
<Window xmlns:l="clr-namespace:GroceryErp.Localization;assembly=GroceryErp.Localization">
  <TextBlock Text="{l:Tr Sales_Pos_Title}" />
  <ui:Button Content="{l:Tr Common_Save}" />
  <DataGridTextColumn Header="{l:Tr Catalog_Product_Name}" Binding="{Binding Name}" />
</Window>
```

`DataGridColumn.Header` is not in the visual tree but `{l:Tr}` still works because the binding has
an explicit `Source`.

**Dynamic text** (counts, totals, messages with values) is exposed by ViewModels:

```csharp
public abstract partial class LocalizedViewModel : PageViewModel,
    IRecipient<LanguageChangedMessage>
{
    protected static TranslationSource T => TranslationSource.Instance;

    protected LocalizedViewModel() => WeakReferenceMessenger.Default.Register(this);

    public void Receive(LanguageChangedMessage m) => OnPropertyChanged(string.Empty); // refresh all
}

// e.g. in PosViewModel
public string ItemsSummary => T.Plural("Sales_Pos_Items", LineCount);  // "3 items" / "3 વસ્તુઓ"
```

**Switching language** (title-bar switcher or Settings): save `ui.language`, set
`TranslationSource.Instance.UiCulture`, publish `LanguageChangedMessage`. The shell then
**re-activates the current page** — the simplest reliable way to refresh converters and row
ViewModels that don't listen for the message. Open bills are unaffected (POS VM is a singleton).

### 5.4 Cultures and formatting

Two cultures are deliberately separate:

| Culture | Controls | Value |
|---|---|---|
| `CurrentUICulture` | Which translation is shown | `ui.language`: `en-IN` (default) or `gu-IN` |
| `CurrentCulture` / `FormatCulture` | All number and date formatting | **Fixed `en-IN` in code** — not a setting, never changes with the language |

`en-IN` gives English digits, lakh/crore grouping (`1,23,45,678.000`) and `₹`. Because the format
culture never changes, numbers look identical in English and Gujarati and parsing is never ambiguous.

Startup, before any window is created:

```csharp
var format = TranslationSource.FormatCulture;                               // fixed en-IN
CultureInfo.DefaultThreadCurrentCulture = format;
CultureInfo.CurrentCulture = format;

// WPF bindings format with FrameworkElement.Language (default en-US!), not CurrentCulture:
FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
    new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(format.IetfLanguageTag)));

TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo(settings.UiLanguage);  // en-IN default
```

`OverrideMetadata` may be called only once per process. Switching to Gujarati changes **only**
`CurrentUICulture` — never `CurrentCulture`, `FormatCulture` or the WPF `Language` property.

All display formatting goes through one service (no `StringFormat` for money in XAML):

```csharp
public sealed class DisplayFormatter
{
    private static CultureInfo F  => TranslationSource.FormatCulture;          // en-IN, always
    private static CultureInfo Ui => TranslationSource.Instance.UiCulture;     // for words only

    public string Money(Money m) =>
        (m.Value < 0 ? "−₹" : "₹") + Math.Abs(m.Value).ToString("N3", F);     // ₹1,23,456.789
    public string Qty(Quantity q, string uomLabel) => $"{q.Value.ToString("N3", F)} {uomLabel}";
    public string Percent(Percent p) => p.Value.ToString("N3", F) + "%";
    public string Count(long n) => n.ToString("N0", F);                        // 1,250
    public string Date(DateOnly d) => d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
    public string DateTime(DateTime t) => t.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);

    /// Month name follows the UI language, the year is always English digits: "સપ્ટેમ્બર 2026".
    public string MonthYear(int year, int month) =>
        $"{Ui.DateTimeFormat.GetMonthName(month)} {year.ToString(CultureInfo.InvariantCulture)}";
}
```

Rules:

- Numbers are formatted with `FormatCulture` or `InvariantCulture` — **never** with `UiCulture`,
  never with a parameterless `ToString()` in presentation code.
- The UI culture is used only for words (translations, month and weekday names).
- `TranslationSource.Format` formats placeholder arguments with `FormatCulture`, so numbers inside
  Gujarati sentences are English digits.
- `N3` honours the culture's group sizes, so en-IN produces Indian grouping. Values always show
  3 decimals (the requirement); `format.always_3dp = 0` optionally shows whole-unit quantities of
  non-fractional units without decimals.
- **Parsing** user input always uses `CultureInfo.InvariantCulture` (`.` decimal separator; commas
  stripped) after `InputNormalizer` has converted any Gujarati digits — identical in both languages.
- Charts (LiveCharts2) get explicit label formatters that call `DisplayFormatter`.
- Excel exports write numbers as numeric cells with explicit formats (`#,##0.000`), dates as
  `dd-mm-yyyy`, so Excel shows English digits regardless of the file's UI language.

### 5.5 Fonts and script rendering

```xml
<!-- GroceryErp.UI.Common/Themes/Typography.xaml -->
<FontFamily x:Key="AppFont">Segoe UI, Nirmala UI</FontFamily>
```

Applied on the shell window root (`FontFamily="{StaticResource AppFont}"`) so it inherits
everywhere. Nirmala UI covers Gujarati on every Windows 10/11 install.

Rules for layouts that must survive Gujarati text:

- **No fixed heights** on text-bearing elements; Gujarati needs extra room above and below
  the baseline (matras, conjuncts). Use `MinHeight`.
- **No fixed widths for labels**; Gujarati strings often run 20–40% longer than English.
  Use grid star-sizing and `TextTrimming="CharacterEllipsis"` with a tooltip as last resort.
- Buttons size to content with a `MinWidth`.
- Test rendering of Gujarati conjuncts and vowel signs on real screens: **ક્ષ, જ્ઞ, શ્ર, દ્ર, ત્ર,
  કૃ, ર્ક**. Check at 100%, 125% and 150% display scaling.

**Digit rendering.** WPF can substitute digit *glyphs* at render time based on culture or the
Windows regional settings (`NumberSubstitution`) — a PC whose Windows region is set to Gujarati
with native digits could otherwise draw `123` as `૧૨૩`. Pin European digits on the shell root and
on every visual that is rendered for printing (receipt, label, A4 pages):

```xml
<ui:FluentWindow x:Class="GroceryErp.App.ShellWindow"
                 FontFamily="{StaticResource AppFont}"
                 NumberSubstitution.CultureSource="Override"
                 NumberSubstitution.CultureOverride="en-IN"
                 NumberSubstitution.Substitution="European">
```

The properties inherit down the element tree. Separate visual roots (`ToolTip`, `ContextMenu`,
`Popup` content, dialogs hosted in their own window) get the same three setters through the
implicit styles in `Themes/Typography.xaml`.

### 5.6 Keyboard input

**Typing Gujarati.** Users type Gujarati names with Windows' built-in *Gujarati Phonetic*
keyboard (type "khand" → ખાંડ). The first-run wizard checks
`InputLanguageManager.Current.AvailableInputLanguages` and, if Gujarati is missing, shows how to
add it (Settings → Time & language → Language & region). `Win+Space` switches layouts.

Gujarati-name fields switch the layout automatically when focused (no effect if not installed):

```xml
<TextBox Text="{Binding NameLocal}"
         InputLanguageManager.InputLanguage="gu-IN"
         InputLanguageManager.RestoreInputLanguage="True" />
```

**Scanner and numeric fields must stay in English layout.** A keyboard-wedge scanner sends key
strokes; with a Gujarati layout active they can arrive as Gujarati digits (૧૨૩) or other
characters. Two defenses:

```xml
<TextBox x:Name="ScanBox"
         InputMethod.IsInputMethodEnabled="False"
         InputLanguageManager.InputLanguage="en-US"
         InputLanguageManager.RestoreInputLanguage="True" />
```

and normalization of every scanned or numeric string:

```csharp
public static class InputNormalizer
{
    private const char GS = '\u001D';   // GS1 group separator — must survive

    /// Maps Gujarati digits ૦–૯ (and any other Unicode decimal digit) to ASCII 0–9,
    /// drops control characters except GS, trims.
    public static string Normalize(string raw)
    {
        var s = raw.Trim();
        var buf = new char[s.Length];
        int n = 0;
        foreach (var ch in s)
        {
            if (ch == GS) { buf[n++] = ch; continue; }
            if (char.IsControl(ch)) continue;
            buf[n++] = char.IsDigit(ch) && ch > '9'
                ? (char)('0' + CharUnicodeInfo.GetDecimalDigitValue(ch))
                : ch;
        }
        return new string(buf, 0, n);
    }
}
```

`InputNormalizer` is applied to **every** text input, not only scan and numeric fields: a
`NormalizeInput` behavior on the shared `TextBox` style converts Gujarati digits as the user types,
and services normalize again before saving (so imports and pasted text are covered too). A phone
number, pincode, address or product name typed with the Gujarati layout ("ચા ૨૫૦ ગ્રામ") is stored
and shown with English digits ("ચા 250 ગ્રામ").

### 5.7 Multilingual data

**Schema** (migration 002): `name_local` on `category`, `expense_category`, `uom`
(`product.name_local` exists since 001); `sale_line.product_name_local` snapshot for bilingual
reprints.

**What is shown where:**

| Context | Displayed |
|---|---|
| Product lists, POS lines, search results | `name` (primary line) + `name_local` (secondary line) |
| Category tree, expense categories, units | UI language Gujarati → `name_local ?? name`; English → `name` |
| Printed bill | per §5.9 |
| Excel exports | both columns |

```csharp
public static class LocalizedText
{
    public static string Pick(string name, string? nameLocal) =>
        TranslationSource.Instance.UiCulture.TwoLetterISOLanguageName == "gu"
        && !string.IsNullOrWhiteSpace(nameLocal) ? nameLocal : name;
}
```

**Codes, not text, in the database.** Statuses, payment modes, movement types, P&L heads, expiry
buckets are stored as codes (`COMPLETED`, `UPI`, `SALES`, `EXPIRED`) and translated in the UI via
`Enum_*` / `Pnl_Head_*` keys:

```csharp
public sealed class CodeToTextConverter : IValueConverter
{
    public string Prefix { get; set; } = "Enum";          // e.g. "Enum_SaleStatus"
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is null ? "" : TranslationSource.Instance[$"{Prefix}_{value}"];
    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
```

Migration 002 rewrote `v_pnl_entry` to return `head_code` + `ref_id` instead of English head names
for this reason, and moved stock adjustments into the P&L.

**Seed data.** Expense categories and units are seeded in English by migration 001. At first run,
the wizard fills their `name_local` from the Gujarati `Seed_ExpenseCategory_*` / `Seed_Uom_*`
resources (whichever UI language was chosen). After that they are ordinary user data.

**Text hygiene on save.** All user text: Gujarati digits → `0–9` (the numbers rule),
`string.Normalize(NormalizationForm.FormC)`. Names, aliases and Gujarati names additionally:
collapse whitespace and remove zero-width joiners/non-joiners (U+200C, U+200D) — they change
nothing a shopkeeper cares about but break substring search.

**Sorting** local names: SQLite `NOCASE` only folds ASCII. Lists sorted by local name are sorted
in C# with `StringComparer.Create(uiCulture, ignoreCase: true)`; SQL `ORDER BY` uses the English
`name`.

### 5.8 Search across scripts

- FTS5 trigram indexes `sku, name, name_local, search_terms, brand` — substring search works for
  Gujarati exactly as for English (verified with `લોટ` in the schema tests).
- Queries are normalized with the same hygiene as stored text (§5.7) plus `InputNormalizer`, so
  "૨૫૦" and "250" find the same products. Normalization never depends on the UI language.
- Queries shorter than 3 characters use prefix `LIKE` on `name` (English) — encourage 3+ chars.
- **Transliteration** is handled by data, not code: put Latin spellings of local names in
  `search_terms` ("khand, cheeni, sakar"). The product form offers a one-click "add Latin alias"
  suggestion generated from a small Gujarati→Latin character map (best-effort; the user
  edits it).

### 5.9 Printing in Gujarati

- Thermal printers' built-in fonts have no Gujarati glyphs (and usually no `₹`).
  Receipts are therefore **rendered as an image** with WPF and sent as ESC/POS raster data (§15.3).
  The same path works for English, so there is one code path; an optional fast text mode
  (`printer.receipt.mode = TEXT`) exists for English-only bills.
- Settings: `print.language` = `en-IN` (default) or `gu-IN` — labels on the bill ("Total", "Qty"…);
  `print.bilingual_names` — print the Gujarati name under the English name.
- **All numbers on receipts, labels and A4 documents are English digits** in both print languages
  (amounts, quantities, rates, MRP, GST %, dates, times, invoice numbers, page numbers). The print
  visuals carry the same `NumberSubstitution` setters as the shell (§5.5).
- Print labels come from resources in the **print culture**, independent of the UI:
  `T.Get("Print_Receipt_Total", printCulture)`.
- Barcode labels (TSPL): barcode, price and English text use printer commands; local names are
  rendered to a bitmap and sent with TSPL `BITMAP` (§15.4).

### 5.10 Errors and messages

- Services throw `DomainException(ErrorCode, args)`; the UI shows `Error_{Code}` formatted with
  `args` in the UI language.
- Validation messages come from `Validation_*` keys.
- Logs are **always English** (they are for the developer).

### 5.11 Translation workflow & QA

1. Developers add keys to the neutral `Strings.resx` only (English text + comment).
2. **ResXManager** shows every key × language in one grid; export to Excel for the translator
   (ideally the store owner or a native-speaking relative who knows shop vocabulary), import back.
3. Follow the glossary below so the same concept always uses the same word.
4. **Pseudo-localization** mode (Settings → Developer, or `--pseudo` switch) transforms every
   string: `Save` → `[!! Śàvé ~~ !!]` — reveals hard-coded strings (they stay plain),
   truncation (brackets cut off) and layout overflow (text is ~40% longer).
5. Automated checks (§18.8): every neutral key exists in `gu`; every `{l:Tr X}` in XAML exists;
   placeholder sets match; no Gujarati digit in any resource; no `⟦key⟧` appears during UI smoke tests.

```csharp
internal static class Pseudo
{
    private static readonly Dictionary<char, char> Map = new()
    { ['a']='à', ['e']='é', ['i']='î', ['o']='ô', ['u']='ü', ['A']='Å', ['E']='É', ['S']='Ś', ['c']='ç' };

    public static string Transform(string s) =>
        "[!! " + new string(s.Select(ch => Map.GetValueOrDefault(ch, ch)).ToArray())
               + new string('~', Math.Max(2, s.Length * 2 / 5)) + " !!]";   // placeholders untouched
}
```

**Glossary (draft — have a Gujarati-speaking shop owner confirm):**

| English | Gujarati | Note |
|---|---|---|
| Bill / Invoice | બિલ | |
| Sales | વેચાણ | |
| Purchase | ખરીદી | |
| Stock | સ્ટોક | |
| **Lot / Batch** | **બેચ** | Never "લોટ" in Gujarati — it means *flour* |
| Expiry date | એક્સપાયરી તારીખ | |
| Supplier / Vendor | સપ્લાયર | |
| Customer | ગ્રાહક | |
| Price / Rate | ભાવ | |
| Quantity | જથ્થો | |
| Discount | વટાવ | |
| Total | કુલ | |
| Damage | ડેમેજ / બગાડ | Avoid નુકસાન — reserved for "loss" (નફો-નુકસાન) |
| Expense | ખર્ચ | |
| Salary | પગાર | |
| Asset | મિલકત | |
| Depreciation | ઘસારો | |
| Profit & Loss | નફો-નુકસાન | |
| Credit (udhaar) | ઉધાર | |
| MRP, GST, HSN, UPI, SKU | unchanged | Keep acronyms in Latin script |
| Numbers | 0–9 | Never ૦–૯, anywhere (§5.1) |

### 5.12 Adding a language later (only by explicit decision)

1. Add `Strings.<lang>.resx` to every module and to `Shared`; translate via ResXManager.
2. Add the culture to the language list in Settings and the title-bar switcher.
3. Verify the font stack covers the script (Nirmala UI covers all major Indian scripts); render
   the conjunct test page.
4. Add `Seed_*` translations; add plural rule to `TranslationSource.Plural` if it differs.
5. Extend the numbers rule: add the new script's digits to the normalizer and resource tests.
6. Print a test receipt and a test label in the new language.
7. Run the i18n test suite and a pseudo-localization pass.

---

## 6. Authentication & first-run setup

### 6.1 First-run wizard

Shown when `app_user` has no row. Each step validates before "Next"; nothing is written until the
final step, which runs one transaction.

| Step | Collects | Notes |
|---|---|---|
| 1. Language | English (pre-selected) or ગુજરાતી | Wizard opens in English; switching changes the UI immediately |
| 2. Store | Name, address, phone, GSTIN, state, tax mode (Regular / Composition / Not registered) | GSTIN validated (§9.1); state pre-filled from GSTIN digits 1–2 |
| 3. Owner login | Username, password (twice) | Clear warning: **there is no password recovery**; suggest writing it down and keeping it safe |
| 4. Data & backups | Data folder (default shown), backup folder, USB reminder frequency | Rejects OneDrive paths |
| 5. Printers | Receipt printer + width (58/80 mm), label printer + label size, A4 printer | "Print test" per printer |
| 6. Barcodes | Internal prefixes (default 20 lot / 21 product), weighing-scale rule (optional) | Validates prefixes don't collide |
| 7. Keyboard | Checks the Gujarati Phonetic keyboard is installed | Instructions if missing |
| 8. Start | Import products from Excel (template download) / start empty | Opening stock can follow later |

Final transaction: insert `app_user`, settings, `name_local` for seeded masters, barcode rule;
then create the first backup.

### 6.2 Login, lock & password

```csharp
public sealed class PasswordHasher
{
    private const int SaltBytes = 16, HashBytes = 32, MemoryKiB = 65_536, Iterations = 3, Lanes = 2;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt);
        return $"argon2id$m={MemoryKiB},t={Iterations},p={Lanes}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');                 // argon2id | params | salt | hash
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        return CryptographicOperations.FixedTimeEquals(Compute(password, salt), expected);
    }

    private static byte[] Compute(string password, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt, MemorySize = MemoryKiB, Iterations = Iterations, DegreeOfParallelism = Lanes,
        };
        return argon.GetBytes(HashBytes);
    }
}
```

(Parse the parameters from `parts[1]` in production so they can be raised later without breaking
old hashes.)

- **Failed attempts:** 1 s delay after 3 failures, doubling to a 30 s cap; reset on success.
- **Idle lock:** `security.idle_lock_minutes` (default 15). A `DispatcherTimer` checks the time
  since the last `PreviewKeyDown`/`PreviewMouseDown` on the shell; locking shows a full-window
  overlay requiring the password. The POS bill and all page state stay in memory.
- **Change password:** Settings → Security; requires the current password.
- **Manual lock:** title-bar button or `Ctrl+Shift+L`.

---

## 7. Catalog

### 7.1 Product rules

| Field | Rule |
|---|---|
| `sku` | Unique, case-insensitive, ≤ 20 chars. Auto-suggested as the next number (`P000124`) but editable — shopkeepers like short memorable codes ("SUG1") for the counter. |
| `name` | Required, ≤ 120 chars, normalized (§5.7). Include size/variant: "Tata Salt 1 kg". |
| `name_local` | Optional but encouraged; the product list can filter "missing local name". |
| `search_terms` | Comma-separated aliases, Latin transliterations, common misspellings. |
| `category_id` | Required. Categories nest at most 3 levels. |
| `uom_id` | Required. Cannot change once the product has lots (quantities would change meaning). |
| `is_loose` | Loose items must use a fractional unit (KG/L) and normally have a `plu_code`. |
| `tax_rate_id`, `hsn_code` | Required when `tax.mode = REGULAR`; hidden otherwise. HSN: 4/6/8 digits. |
| `track_expiry` | Default on; off for items like utensils or plastic bags. |
| `default_shelf_life_days` | Pre-fills lot expiry on receipt (bread, milk, paneer). |
| `reorder_level` | In base units; 0 = no low-stock alert. |
| `default_selling_price`, `default_mrp` | Pre-fill new lots; the live price is always the lot's. |

### 7.2 Add / edit / remove

- **Add:** form with sections Basic · Pricing defaults · Tax · Stock & expiry · Barcodes. Saving
  creates the product and any barcodes in one transaction. "Save & new" keeps category/brand/unit.
- **Edit:** all fields except `uom_id` after lots exist. Changes are audited (§3.12). Renames do
  not change past bills (they snapshot names).
  The editor preserves pack quantity, primary flag, symbology and source on unchanged barcodes,
  and exposes the product expiry-alert override. Removing a primary barcode promotes the first
  remaining barcode.
- **Remove:**
  - If the product has **no** lots, purchase lines, sale lines or damages → hard delete allowed
    (FTS trigger cleans the index).
  - Otherwise → **archive** (`is_active = 0`): hidden from POS and search by default, visible under
    the "Archived" filter, can be restored. The UI calls both actions "Remove" and explains which
    one happened.

```sql
SELECT EXISTS(SELECT 1 FROM product_lot   WHERE product_id = @id)
    OR EXISTS(SELECT 1 FROM purchase_line WHERE product_id = @id)
    OR EXISTS(SELECT 1 FROM sale_line     WHERE product_id = @id)
    OR EXISTS(SELECT 1 FROM damage        WHERE product_id = @id) AS has_history;
```

### 7.3 Product list

- Loads all active products (fast: ~20k rows) with stock from `v_product_stock`.
  The shell list requests up to 20,000 rows and virtualizes the grid; a stale search response
  cannot replace newer results. A zero reorder threshold does not count as low stock.
- Filters: search box · category tree · brand · low stock · no barcode · missing local name ·
  archived · loose items.
- Columns: SKU · Name (+ local) · Category · Stock · Price (current FEFO lot) · MRP · Status.
- Row actions: Edit · Lots · Stock ledger · Print labels · Archive.
- Bulk actions: print labels, change category, change tax rate (audited).

### 7.4 Categories, brands, units, tax rates

Simple master screens with inline editing. Categories: tree view with drag-to-reparent (max depth
3), `expiry_alert_days` override, `name_local`. Units: code, name, `name_local`, `allow_fraction`
(locked once used). Tax rates: name, rate; deactivate instead of delete once used.

### 7.5 Price changes

Prices live on lots. "Change price" on a product opens a dialog listing its lots with stock:
new selling price (and optionally MRP) applied to selected lots, validated `≤ MRP`, audited as
`PRICE_CHANGE`, and optionally copied to `default_selling_price`. A "print new labels" checkbox
queues labels for the affected lots.

### 7.6 Bulk import (Excel)

Template columns (downloadable from the wizard and Products → Import):

`SKU* | Name* | Local name | Aliases | Category* | Subcategory | Brand | Unit* | HSN | GST % |
Barcode | Selling price | MRP | Reorder level | Shelf life days | Loose (Y/N) | PLU | Track expiry (Y/N)`

Process:

1. Read with ClosedXML; normalize text (§5.7), parse numbers invariantly (§5.4).
2. **Dry run:** validate every row (required fields, unit exists, GST % matches a tax rate,
   barcode check digit, duplicates within file and against DB) → preview grid with per-row errors.
3. User fixes the file or unticks bad rows; errors exportable as a sheet.
   Invalid rows are deselected and cannot be checked for import. The importer rechecks unit
   compatibility and barcode ownership inside the posting transaction after the dry run.
4. **Import** in one transaction: create missing categories/brands, upsert by SKU (update if
   exists), add barcodes. Summary: created / updated / skipped.

Opening stock has its own template (§10.6).

---

## 8. Barcodes & scanning

### 8.1 Scanner setup (document for the store)

Program the scanner once using its manual's configuration barcodes:

| Setting | Value |
|---|---|
| Interface | USB HID keyboard |
| Keyboard country | US |
| Suffix | Enter (CR) |
| Prefix | none (or a rare character, if you want deterministic detection — §8.2) |
| EAN-13 / EAN-8 / UPC-A / Code 128 | enabled, transmit check digit |
| UPC-A | transmit as 12 digits (the resolver also tries the 13-digit form) |
| GS1 DataMatrix / GS1-128 (2D models) | enabled; transmit AIM symbology ID; GS as `0x1D` |
| Inter-character delay | 0 |

### 8.2 Detecting scans

Primary path: the POS keeps focus on the **scan box**; after any dialog closes, focus returns there.

Fallback: a window-level burst detector recognizes scans even if focus is elsewhere (e.g. the
quantity box) and undoes the characters the scanner typed into that field:

```csharp
public sealed class ScanBurstDetector
{
    private readonly int _maxGapMs, _minLength;           // settings: scanner.max_gap_ms (35), scanner.min_length (6)
    private readonly StringBuilder _buf = new();
    private long _last;
    private TextBox? _target;
    private string? _snapshot;
    private int _caret;

    public event Action<string>? Scanned;

    public ScanBurstDetector(int maxGapMs, int minLength) => (_maxGapMs, _minLength) = (maxGapMs, minLength);

    public void Attach(Window w)
    {
        w.PreviewTextInput += OnText;
        w.PreviewKeyDown += OnKey;
    }

    private void OnText(object? sender, TextCompositionEventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_buf.Length == 0 || Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds > _maxGapMs)
        {
            _buf.Clear();
            _target = Keyboard.FocusedElement as TextBox;
            _snapshot = _target?.Text;
            _caret = _target?.CaretIndex ?? 0;
        }
        _buf.Append(e.Text);
        _last = now;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        bool isBurst = _buf.Length >= _minLength &&
                       Stopwatch.GetElapsedTime(_last).TotalMilliseconds <= _maxGapMs;
        if (!isBurst) { _buf.Clear(); return; }

        if (_target is not null && _snapshot is not null)
        {
            _target.Text = _snapshot;                     // undo scanner keystrokes in that field
            _target.CaretIndex = Math.Min(_caret, _snapshot.Length);
        }
        var code = _buf.ToString();
        _buf.Clear();
        e.Handled = true;                                  // Enter must not trigger a default button
        Scanned?.Invoke(code);
    }
}
```

Humans rarely type 6+ characters with < 35 ms between each; scanners send a code in a few ms per
character. Both thresholds are settings so a slow scanner can be accommodated.

### 8.3 EAN arithmetic

```csharp
public static class Ean
{
    /// Check digit for EAN-8/UPC-A/EAN-13/GTIN-14 bodies: weights 3,1,3,1… from the rightmost digit.
    public static int CheckDigit(ReadOnlySpan<char> body)
    {
        int sum = 0;
        for (int i = body.Length - 1, w = 3; i >= 0; i--, w = 4 - w)
            sum += (body[i] - '0') * w;
        return (10 - sum % 10) % 10;
    }

    public static bool IsValid(ReadOnlySpan<char> code) =>
        code.Length is 8 or 12 or 13 or 14
        && AllDigits(code)
        && CheckDigit(code[..^1]) == code[^1] - '0';

    /// In-store EAN-13: 2-digit prefix (20–29) + 10-digit serial + check digit.
    public static string Internal(string prefix, long serial)
    {
        if (prefix.Length != 2 || prefix[0] != '2') throw new ArgumentException("Prefix must be 20–29.");
        if (serial is < 0 or > 9_999_999_999) throw new ArgumentOutOfRangeException(nameof(serial));
        var body = prefix + serial.ToString("D10", CultureInfo.InvariantCulture);
        return body + (char)('0' + CheckDigit(body));
    }

    private static bool AllDigits(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (c is < '0' or > '9') return false;
        return true;
    }
}
```

Test vectors: `4006381333931` valid; `Internal("20", 1)` = `2000000000015`;
`Internal("21", 45)` = `2100000000456`; scale label `2700123012505` valid.

**Internal barcode policy**

| Need | Barcode | Serial source |
|---|---|---|
| Product without a manufacturer barcode (loose items packed in-store, local brands) | `21` + product id + check | `product.id` |
| Lot-specific label (same EAN, different MRP/price in stock at once) | `20` + lot id + check | `product_lot.id` |

Generated barcodes are inserted into `product_barcode` with `source = 'INTERNAL'` at the moment the
user chooses "Generate barcode" / "Print lot labels".

### 8.4 Resolving a scan

```csharp
public sealed class ScanResolver(IBarcodeLookup barcodes, IBarcodeRules rules, IProductLookup products)
{
    public ScanResult Resolve(string raw)
    {
        var code = InputNormalizer.Normalize(raw);
        if (code.Length == 0) return ScanResult.Empty;

        // 1. Exact match: manufacturer, carton, internal product or lot barcode
        if (barcodes.Find(code) is { } hit)
            return ScanResult.Item(hit.ProductId, hit.LotId, hit.PackQty, code);

        // 2. UPC-A ↔ EAN-13 equivalence (leading zero)
        if (code.Length == 12 && barcodes.Find("0" + code) is { } a) return ScanResult.Item(a.ProductId, a.LotId, a.PackQty, code);
        if (code.Length == 13 && code[0] == '0' && barcodes.Find(code[1..]) is { } b) return ScanResult.Item(b.ProductId, b.LotId, b.PackQty, code);

        // 3. Weighing-scale label (variable measure)
        foreach (var rule in rules.Active)
            if (rule.TryParse(code, out var plu, out var value) && products.FindByPlu(plu) is { } p)
                return rule.Kind == ValueKind.Weight
                    ? ScanResult.Weighed(p.Id, new Quantity(value), code)
                    : ScanResult.Priced(p.Id, new Money(value), code);

        // 4. GS1 element string from 2D / GS1-128 codes
        if (Gs1.TryParse(code, out var gs1) && gs1.Gtin is { } gtin)
        {
            var ean = gtin.StartsWith('0') ? gtin[1..] : gtin;          // GTIN-14 → EAN-13
            if (barcodes.Find(ean) is { } g)
                return ScanResult.Item(g.ProductId, products.FindLot(g.ProductId, gs1.Batch, gs1.Expiry),
                                       g.PackQty, code);
        }

        // 5. SKU typed at the counter
        if (products.FindBySku(code) is { } bySku)
            return ScanResult.Item(bySku.Id, null, Quantity.One, null);

        return ScanResult.NotFound(code);
    }
}
```

`barcodes.Find` is a single indexed lookup (`product_barcode.barcode` is UNIQUE); the whole resolve
takes well under a millisecond.

### 8.5 Weighing-scale rules

```csharp
public sealed record BarcodeRule(string Prefix, int TotalLength, int ItemStart, int ItemLength,
                                 int ValueStart, int ValueLength, ValueKind Kind, long ValueScale)
{
    public bool TryParse(string code, out string plu, out decimal value)
    {
        plu = ""; value = 0;
        if (code.Length != TotalLength || !code.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (TotalLength == 13 && !Ean.IsValid(code)) return false;
        plu = code.Substring(ItemStart - 1, ItemLength).TrimStart('0');
        if (!long.TryParse(code.AsSpan(ValueStart - 1, ValueLength), NumberStyles.None,
                           CultureInfo.InvariantCulture, out var raw)) return false;
        value = Fx3.FromScaled(raw * ValueScale);
        return true;
    }
}
```

Example `2700123012505`, rule `prefix 27, item 3–7, value 8–12, WEIGHT, scale 1`: PLU `123`,
weight `01250` g → 1.250 kg. PLUs are stored without leading zeros in `product.plu_code`.

For **PRICE** labels the POS derives quantity = price ÷ lot selling price (rounded to 3 dp) and
keeps the scanned price as the line amount, so the bill matches the label exactly.

### 8.6 GS1 parser (Phase 2 feature)

Supports AIs `(01)` GTIN-14 (fixed 14), `(17)` expiry YYMMDD (fixed 6; day `00` = last day of
month), `(10)` batch (variable ≤ 20, ends at GS or end), `(21)` serial (variable ≤ 20). Strip an
AIM prefix (`]d2`, `]C1`, `]Q3`) if present; without one, only accept strings starting with `01`
and at least 16 characters. The matched lot is the one whose `batch_no` or `expiry_date` equals
the scanned value; otherwise FEFO applies.

### 8.7 Unknown and duplicate barcodes

- **Not found at POS:** dialog "Unknown barcode 890…" → *Quick add product* (barcode pre-filled,
  minimal fields: name, category, unit, price, MRP, tax) → optional quick opening stock → line is
  added to the bill. Or *Search existing product* and attach this barcode to it.
- **Duplicate:** attaching a barcode that belongs to another product is refused with the owner's
  name and a link to it. (Two products can never share a barcode; re-packed manufacturer codes are
  resolved by attaching the code to the right product.)

Implemented in the POS: unresolved scans open a localized dialog with a short, barcode-prefilled
product form, optional opening quantity/cost/expiry, and searchable attachment to an active
product. A conflicting attachment shows the barcode owner's name and opens that product. A new
product with opening stock is added to the current bill; leaving stock blank saves the product
without adding an unsellable line. The attachment and lot-barcode writes are idempotent.

### 8.8 Label printing flow

From product list, purchase posting ("print labels for received lots") or lot screen:
select items → quantity of labels each (default = received qty for lots) → template (price label /
barcode + price / shelf-edge) → preview → print via TSPL (§15.4).
The product-list action accepts multiple selected products. The batch dialog shows product and
active in-stock lot rows, per-row copy counts (received quantity by default for lots), a real
Code 128 preview, and price, barcode-plus-price and shelf-edge templates. Received lots are
offered immediately after purchase posting, and the product's lot view can open the same dialog.
Generated lot EAN-13 codes persist and resolve to the exact lot at POS. TSPL output embeds
Gujarati names as monochrome `BITMAP` data for printer-independent text shaping; large copy
counts are split into printer-sized commands. `.prn` export and RAW Windows printing share the
same batch bytes. Physical printer calibration and scanning remain sign-offs.

---

## 9. Vendors & purchasing

### 9.1 Vendors

Form: name, contact, phones, email, address, city, state, pincode, GSTIN, PAN, UPI, bank details,
payment terms, opening balance, notes. Validation:

```csharp
public static class IndianIds
{
    public static readonly Regex Gstin = new(@"^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$", RegexOptions.Compiled);
    public static readonly Regex Pan   = new(@"^[A-Z]{5}[0-9]{4}[A-Z]$", RegexOptions.Compiled);
    public static readonly Regex Pin   = new(@"^[1-9][0-9]{5}$", RegexOptions.Compiled);
    public static string StateCodeOf(string gstin) => gstin[..2];      // "24" = Gujarat
}
```

PAN is auto-filled from GSTIN characters 3–12. Vendor screen tabs: Details · Purchases · Payments ·
Ledger (opening balance + posted purchases − payments, running balance) · Lots supplied.

### 9.2 Purchase entry (goods receipt)

Header: vendor · vendor invoice no. · invoice date · received date · **vendor bill total (as printed)**
· other charges · notes.

Lines grid (keyboard-driven; scan or search to add a row):

| Column | Default / rule |
|---|---|
| Product | scan/search |
| Batch no. | optional |
| Mfg date | optional |
| Expiry | required if `track_expiry`; pre-filled from `received + default_shelf_life_days` |
| Qty | base units; carton barcode scans multiply by `pack_qty` |
| Free qty | scheme quantity ("10+1") |
| Rate | ex-GST per base unit; pre-filled with the last purchase rate from this vendor |
| Discount | amount (or % converted to amount) |
| GST % | from product's tax rate; editable |
| MRP | pre-filled from last lot |
| Selling price | pre-filled from last lot / product default; ≤ MRP |
| Landed cost | computed live (read-only) |
| Margin % | (selling ex-tax − landed) ÷ selling ex-tax — live, red if negative |

Footer: subtotal, discount, tax, other charges, round-off, **computed total vs vendor bill total**
with the difference highlighted — the fastest way to catch typing mistakes.

Warnings (non-blocking): expiry within the alert window ("short expiry"), selling price below
landed cost, rate differs > 10% from last purchase. Errors (blocking): selling price > MRP, missing
expiry on tracked products, zero quantity, duplicate vendor invoice number.

### 9.3 Landed cost

```csharp
public static class LandedCost
{
    /// Per-unit landed cost for each line: (qty × rate − discount + share of other charges
    /// [+ tax when no input credit]) ÷ (qty + free qty).
    public static IReadOnlyList<Money> Compute(IReadOnlyList<PurchaseLineInput> lines,
                                               Money otherCharges, bool taxIsCost)
    {
        var bases  = lines.Select(l => l.Rate * l.Qty - l.Discount).ToList();
        var shares = MoneyMath.Allocate(otherCharges, bases);
        return lines.Select((l, i) =>
        {
            var total = bases[i] + shares[i] + (taxIsCost ? l.TaxAmount : Money.Zero);
            return total.DivideBy(l.Qty + l.FreeQty);
        }).ToList();
    }
}
```

`taxIsCost = tax.mode != REGULAR` — composition and unregistered stores cannot claim input tax
credit, so GST paid is part of cost.

### 9.4 Posting a purchase

One transaction:

```
PeriodGuard.EnsureOpen(received_date)
load purchase (must be DRAFT) + lines; validate all rules again
compute line tax, totals and landed costs
doc_no = DocNumbers.Next(PURCHASE, received_date)
for each line:
    lot_code = next LOT number
    INSERT product_lot (product, lot_code, batch, purchase_line_id, vendor, dates,
                        unit_cost = landed, mrp, selling_price, qty_received = qty + free)
    IStockLedger.Post(PURCHASE, +(qty + free), unit_cost = landed, ref = purchase_line)
    if a new manufacturer barcode was scanned for this product → INSERT product_barcode
UPDATE purchase SET status = 'POSTED', doc_no, totals, posted_at
audit
```

After commit: offer "Print labels for these lots".

### 9.5 Cancelling a purchase

Allowed only if **every lot from it is untouched** (`qty_on_hand = qty_received` and no other
movements). Posts `PURCHASE_CANCEL` movements for the full quantities, blocks the lots
(`is_active = 0`), sets `status = 'CANCELLED'`, audits. If any lot was sold from, the UI explains
the alternatives: stock adjustment or (backlog) vendor return.

### 9.6 Vendor payments

Form: vendor, date, amount, mode, reference, optional purchase. The vendor ledger and
`v_vendor_balance` show outstanding amounts; the dashboard shows total payables.

---

## 10. Inventory

### 10.1 Screens

- **Stock overview:** products with stock qty, stock value at cost, reorder level, low-stock flag.
- **Lots of a product:** lot code, batch, received, expiry (with bucket chip), cost, MRP, price,
  received, on hand, status; actions: change price, block/unblock, print labels, write off.
- **Stock ledger:** movements for a product or lot with running balance, filterable by date/type,
  each row linking to its source document (`ref_type`/`ref_id`).

```sql
SELECT m.moved_at, m.move_date, m.type_code, m.qty, m.unit_cost, m.ref_type, m.ref_id, l.lot_code,
       SUM(m.qty) OVER (PARTITION BY m.lot_id ORDER BY m.id) AS lot_balance
FROM stock_movement m JOIN product_lot l ON l.id = m.lot_id
WHERE m.product_id = @productId AND m.move_date BETWEEN @from AND @to
ORDER BY m.id;
```

(The running balance must be computed over the full history of the lot; when filtering by date,
start from the balance before `@from`.)

### 10.2 The ledger service

```csharp
public sealed class StockLedger(ISettings settings) : IStockLedger
{
    public void Post(DbTx t, StockMovementInput m)
    {
        t.Connection.Execute("""
            INSERT INTO stock_movement(move_date, product_id, lot_id, type_code, qty, unit_cost, ref_type, ref_id, note)
            VALUES (@Date, @ProductId, @LotId, @Type, @Qty, @UnitCost, @RefType, @RefId, @Note)
            """, m, t.Transaction);
        // trg_move_direction checks the sign, trg_move_apply updates qty_on_hand,
        // trg_lot_no_negative enforces stock.allow_negative.
    }
    // AllocateFefo: §10.3
}
```

Services publish `StockChangedMessage` **after** their transaction commits.

### 10.3 FEFO allocation

```csharp
public IReadOnlyList<LotAllocation> AllocateFefo(DbTx t, long productId, Quantity requested,
    DateOnly asOf, IReadOnlyDictionary<long, Quantity> alreadyReserved)
{
    var lots = t.Connection.Query<LotStock>("""
        SELECT id AS LotId, qty_on_hand AS OnHand, expiry_date AS Expiry,
               selling_price AS Price, mrp AS Mrp, unit_cost AS Cost
        FROM product_lot
        WHERE product_id = @productId AND is_active = 1 AND qty_on_hand > 0
          AND (expiry_date IS NULL OR expiry_date >= @asOf)
        ORDER BY expiry_date NULLS LAST, received_date, id
        """, new { productId, asOf }, t.Transaction).ToList();

    var result = new List<LotAllocation>();
    var remaining = requested;
    foreach (var lot in lots)
    {
        var available = lot.OnHand - alreadyReserved.GetValueOrDefault(lot.LotId);
        if (available <= Quantity.Zero) continue;
        var take = remaining < available ? remaining : available;
        result.Add(new LotAllocation(lot, take));
        remaining -= take;
        if (remaining == Quantity.Zero) return result;
    }

    if (!settings.AllowNegativeStock)
        throw new DomainException(ErrorCode.InsufficientStock, requested - remaining);

    // Negative stock allowed: put the shortfall on the newest lot (most likely the unrecorded stock).
    var newest = NewestLot(t, productId) ?? throw new DomainException(ErrorCode.NoLotForProduct);
    result.Add(new LotAllocation(newest, remaining));
    return result;
}
```

- `alreadyReserved` = quantities of each lot already on the **current bill** (not yet posted).
- **Expired lots are excluded** (`pos.block_expired = 1`, default). Scanning a lot barcode of an
  expired lot shows `ExpiredLot` with the option to write it off.
- A request spanning two lots returns two allocations → two bill lines (each with its own
  price/MRP), which the POS shows grouped under the product.

### 10.4 Stock count (physical verification)

1. **Create count sheet:** choose categories (or all); the sheet snapshots expected qty per lot.
2. **Enter counts:** scan/search products, type counted qty per lot (or per product — distributed
   FEFO-first). Printable count sheet for counting on paper.
3. **Review:** differences with value at cost (shortage = loss, excess = gain), sorted by value.
4. **Post:** one transaction of `ADJUSTMENT` movements with a reason ("Stock count 22-09-2026"),
   audited. Adjustments appear in P&L under **Stock adjustments** (migration 002).

Sales during a count: counting is done outside opening hours, or the sheet records the ledger
position at snapshot time and posts `counted − (expected + movements since snapshot)`.

### 10.5 Manual adjustment

Single-lot `ADJUSTMENT` with a mandatory reason, for corrections outside a full count.

### 10.6 Opening stock (go-live)

Excel template: `SKU* | Qty* | Unit cost* | Selling price* | MRP | Expiry | Batch | Received date`.
Dry run → import creates one lot per row with an `OPENING` movement. Opening stock does not touch
P&L (it's the starting inventory). Run once at go-live; later corrections use adjustments.

### 10.7 Stock valuation

At cost (Σ qty × lot cost) — the accounting number; at selling price and at MRP — for insurance and
owner insight. Report grouped by category with totals; as-of-today only (historical valuation =
backlog: replay the ledger to a date).

### 10.8 Integrity

At startup, `SELECT * FROM v_lot_cache_mismatch` must return no rows. If it does: log every row,
show a banner, and offer **Rebuild stock cache**:

```sql
UPDATE product_lot
SET qty_on_hand = COALESCE((SELECT SUM(qty) FROM stock_movement m WHERE m.lot_id = product_lot.id), 0)
WHERE id IN (SELECT lot_id FROM v_lot_cache_mismatch);
```

(A mismatch means a bug or someone edited the DB by hand; the ledger is the source of truth.)

---

## 11. Sales / POS

### 11.1 Screen layout (1366×768 and up)

```
┌───────────────────────────────────────────────────────────┬──────────────────────────┐
│ [ Scan or search… (F3) ]                  Bill: Draft #41 │  Customer: Walk-in (F6)  │
├───────────────────────────────────────────────────────────┤                          │
│ #  Item                         Qty      Rate    Disc  Amt│  Items        7          │
│ 1  Tata Salt 1 kg               2.000   28.000  0.000 56.0│  Gross    ₹1,284.000     │
│    ટાટા મીઠું · B#2451 · exp 03-2027                       │  Discount   ₹20.000      │
│ 2  Loose Sugar                  1.250   44.000  0.000 55.0│  GST        ₹61.905      │
│    ખાંડ · weighed                                          │  Round off  −₹0.095      │
│ …                                                         │  ─────────────────────   │
│                                                           │  PAY   ₹1,264.000        │
│                                                           │  You saved ₹96.000       │
├───────────────────────────────────────────────────────────┤  [F9 Cash] [F10 UPI]     │
│ Last: Tata Salt 1 kg · stock 46 · MRP 30   Printer ● ready│  [F11 Split] [F12 Done]  │
└───────────────────────────────────────────────────────────┴──────────────────────────┘
```

- Lines show the English name, then local name · batch · expiry on a second line.
- Lines of the same product from different lots are visually grouped.
- The status bar shows the last scanned item's remaining stock and MRP, and printer status.
- Held bills appear as chips above the grid (F8 to open the list).

### 11.2 Bill lifecycle

```
DRAFT ──F7 hold──► HELD ──F8 resume──► DRAFT
  │                                      │
  └────────────F12 complete─────────────►┴──► COMPLETED ──void──► VOID
                                                   │
                                                   └──returns (separate documents)
```

- A `sale` row with status `DRAFT` is created on the first scan; **every change is saved
  immediately** (line insert/update/delete, bill discount, customer). Power cut → nothing lost.
- On startup, existing `DRAFT` bills are offered for resumption; `HELD` bills stay in the held list.
- Drafts older than 7 days can be discarded from Settings → Maintenance (they hold no stock).

### 11.3 Adding items

1. Scan (§8) or pick from search.
2. Quantity: default 1 (or the pack quantity of a carton barcode, or the weight from a scale
   label). **Quantity prefix:** type `3*` then scan → 3 units; `1.25*` works for loose items.
3. `AllocateFefo` with the quantities already on this bill as `alreadyReserved`.
4. Each allocation merges into an existing line of the same lot or creates a new line.
5. Validation: archived product → blocked; `allow_fraction = 0` and fractional qty →
   `FractionalQtyNotAllowed`; insufficient stock → message with "sell anyway" only when
   `stock.allow_negative = 1`.

Editing a line's quantity (F4) re-runs allocation for that product (lines may split or merge).
**Ctrl+L** opens the lot picker for the selected line (shows each lot's price, MRP, expiry, stock)
— used when the customer picks a pack with a different MRP.

### 11.4 Pricing engine

Pure function in `GroceryErp.Core` — called on every cart change for display and again inside the
completion transaction (the UI's numbers are never trusted).

```csharp
public sealed record CartLineInput(
    long ProductId, long LotId, Quantity Qty, Money UnitPrice, Money? Mrp,
    Percent TaxRate, bool PriceIncludesTax, Money LineDiscount, Money UnitCost);

public sealed record PricedLine(
    CartLineInput Input, Money Gross, Money Discount, Money Taxable, Money Tax, Money Total, Money Cost);

public sealed record PricedBill(
    IReadOnlyList<PricedLine> Lines, Money Gross, Money Discount, Money Taxable, Money Tax,
    Money RoundOff, Money GrandTotal, Money Cost, Money MrpSavings);

public static class PricingEngine
{
    public static PricedBill Price(IReadOnlyList<CartLineInput> lines, Money billDiscount, Money roundStep)
    {
        var gross = lines.Select(l => l.UnitPrice * l.Qty).ToList();
        var afterLine = lines.Select((l, i) => gross[i] - l.LineDiscount).ToList();
        if (afterLine.Any(v => v < Money.Zero)) throw new DomainException(ErrorCode.DiscountExceedsLine);

        var subtotal = Sum(afterLine);
        if (billDiscount > subtotal) throw new DomainException(ErrorCode.DiscountExceedsLine);
        var billShares = MoneyMath.Allocate(billDiscount, afterLine);

        var priced = new List<PricedLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var net = afterLine[i] - billShares[i];
            var (taxable, tax) = l.PriceIncludesTax
                ? Tax.FromInclusive(net, l.TaxRate)
                : Tax.FromExclusive(net, l.TaxRate);
            priced.Add(new PricedLine(l, gross[i], l.LineDiscount + billShares[i],
                                      taxable, tax, taxable + tax, l.UnitCost * l.Qty));
        }

        var total    = Sum(priced.Select(p => p.Total));
        var roundOff = MoneyMath.RoundOff(total, roundStep);
        var atMrp    = Sum(priced.Select(p => p.Input.Mrp is { } mrp ? mrp * p.Input.Qty : p.Gross));
        var savings  = atMrp - (total + roundOff);

        return new PricedBill(priced, Sum(gross), Sum(priced.Select(p => p.Discount)),
            Sum(priced.Select(p => p.Taxable)), Sum(priced.Select(p => p.Tax)),
            roundOff, total + roundOff, Sum(priced.Select(p => p.Cost)),
            savings > Money.Zero ? savings : Money.Zero);
    }

    private static Money Sum(IEnumerable<Money> xs) => xs.Aggregate(Money.Zero, (a, b) => a + b);
}
```

Notes:

- With `tax.mode` = COMPOSITION or NONE the service passes `TaxRate = 0` for every line.
- `PriceIncludesTax` comes from `tax.prices_include_tax` (MRP-based retail is tax-inclusive) and is
  snapshotted on the line (migration 002).
- Line discount: amount or % (converted to an amount on entry). Bill discount (F6-F6 / Ctrl+D):
  amount or %; stored as entered in `sale.bill_discount`, allocated at pricing time.
- Price override: editable unit price (≤ MRP), audited. Selling above MRP is impossible (DB CHECK
  on lots + service check on overrides).

### 11.5 Payment

Payment dialog (F9 opens with Cash selected and the amount pre-filled):

- **Cash:** type tendered amount → change shown in large text.
- **UPI:** optional reference (last 4–6 digits of the transaction ID).
- **Card:** optional last 4 digits.
- **Split (F11):** add rows (mode + amount) until the remaining amount is 0.
- **Credit:** hidden until the customer-credit ledger exists (§21.3).
- Rules: Σ payments ≥ grand total; overpayment only allowed when a Cash row exists (change is cash).

### 11.6 Completing a bill

```csharp
public Task<CompletedSale> CompleteAsync(CompleteSaleCommand cmd) =>   // SaleId, Payments, OpId
    db.WriteAsync(t =>
    {
        if (OperationGuard.Claim(t, cmd.OpId, OpType.SaleComplete) is { RefId: long doneId })
            return repo.LoadCompleted(t, doneId) with { IsReplay = true };   // no second sale, no reprint

        var (saleId, payments) = (cmd.SaleId, cmd.Payments);
        var sale = repo.Get(t, saleId) ?? throw new DomainException(ErrorCode.NotFound);
        if (sale.Status is not (SaleStatus.Draft or SaleStatus.Held))
            throw new DomainException(ErrorCode.SaleFinalized);
        periodGuard.EnsureOpen(sale.SaleDate);

        var lines = repo.GetLines(t, saleId);
        if (lines.Count == 0) throw new DomainException(ErrorCode.EmptyBill);

        var bill = PricingEngine.Price(lines.Select(ToInput).ToList(), sale.BillDiscount, settings.RoundStep);   // bill.round_to

        var paid = payments.Aggregate(Money.Zero, (a, p) => a + p.Amount);
        if (paid < bill.GrandTotal) throw new DomainException(ErrorCode.PaymentShort);
        var change = paid - bill.GrandTotal;
        if (change > Money.Zero && payments.All(p => p.Mode != PaymentMode.Cash))
            throw new DomainException(ErrorCode.OverpaymentNonCash);

        var invoiceNo = docNumbers.Next(t, DocType.Sale, sale.SaleDate);
        repo.UpdateLineAmounts(t, bill.Lines);                 // while still DRAFT (trigger-safe)
        foreach (var line in bill.Lines)
            ledger.Post(t, StockMovementInput.Sale(sale.SaleDate, line, saleId));
        repo.InsertPayments(t, saleId, payments);
        repo.MarkCompleted(t, saleId, invoiceNo, bill, paid, change, clock.Now);  // status last
        OperationGuard.Complete(t, cmd.OpId, "sale", saleId);
        return new CompletedSale(saleId, invoiceNo, bill, change);
    });
```

The POS creates the completion `OpId` when a bill starts, so pressing F12 twice completes it once.
The status check (`SaleFinalized`) remains as a second guard.

After commit (outside the transaction — a printer problem must never roll back a sale; skipped
when `IsReplay`):

1. Print the receipt (§15.3) — on failure: Snackbar "Printer not ready — Ctrl+P to reprint".
2. Open the cash drawer if any Cash payment.
3. Publish `SaleCompletedMessage`; start a new empty bill; focus the scan box.

### 11.7 Hold, resume, void, reprint

- **Hold (F7):** status `HELD`, optional label ("Mrs. Shah"); new bill starts.
- **Resume (F8):** list of held bills with time, items, total; resuming re-validates stock and
  prices (a lot may have sold out or changed price meanwhile — changes are shown before continuing).
- **Void:** from sales history; requires a reason; by default only for bills of the current business
  date (`pos.void_same_day_only`); not allowed if the bill has returns. Transaction: `SALE_VOID`
  movements (+qty back to the same lots), status `VOID`, `void_reason`, `voided_at`, audit.
  Voided bills keep their invoice numbers (the series stays gapless) and are excluded from P&L.
- **Reprint (Ctrl+P last bill, or from history):** identical content from snapshots, marked
  "DUPLICATE".

### 11.8 Returns

1. Find the original bill (scan the barcode printed on the receipt — the invoice number as
   Code 128 — or search by number/date/amount).
2. Select lines and quantities (≤ sold − already returned), mark each as resellable or not.
3. Amounts per returned line, pro-rata from the original line's snapshot:
   `taxable = Round(orig.taxable × q / orig.qty)`, `tax = Round(orig.tax × q / orig.qty)`,
   `cost = Round(orig.cost × q / orig.qty)`. When the return completes a line (all remaining qty),
   use *original − already returned* instead, so totals reconcile exactly.
4. Refund mode (Cash/UPI/Card/Credit note).
5. One transaction: `sale_return` (+ `R2627-…` number), lines, `SALE_RETURN` movements (+qty into
   the original lots); for non-resellable lines, immediately a `damage` (reason `CUSTOMER_RETURN`)
   with a `DAMAGE` movement.
6. Print a return receipt.

### 11.9 Receipt content

| Block | Content |
|---|---|
| Header | Store name (both languages if bilingual), address, phone, GSTIN |
| Title | **Tax Invoice** (regular GST) · **Bill of Supply** (composition, with the line "Composition taxable person, not eligible to collect tax on supplies") · **Bill** (not registered) |
| Meta | Invoice no., date & time, customer (if any) |
| Lines | Name (+ local name), qty × rate, MRP, amount |
| Totals | Gross, discount, round off, **total**, **"You saved ₹X"** (vs MRP) |
| Tax summary | Per GST rate: taxable, CGST, SGST (regular GST only) |
| Payments | Mode, amount, tendered, change |
| Footer | Custom message ("Thank you! Visit again"), return policy, invoice no. as Code 128 barcode |

### 11.10 Day-end summary

Report for a business date: bills count, gross, discounts, net, GST, round-off, by payment mode,
voids (count/value), returns (count/value), cash expected in drawer (cash sales − cash refunds −
cash expenses entered for the day). Printable on the receipt printer. A full cash-register
session module (opening float, counted cash, difference) is backlog (§21.3).

### 11.11 Sales history

Keyset-paginated list (§4.5) with filters (date range, status, payment mode, amount, invoice no.);
detail view shows lines, payments, returns, movements; actions: reprint, void, return, export.

---

## 12. Damages, expenses & salary

### 12.1 Damages

- **Entry:** scan/search product → pick lot (FEFO order, expired first) → qty → reason
  (`EXPIRED`, `BROKEN`, `SPOILED`, `PEST`, `LOST`, `CUSTOMER_RETURN`, `OTHER`) → notes →
  recovered amount (vendor credit or scrap value, default 0).
- **Value:** `cost_value = Round(qty × lot.unit_cost)` — computed, not typed.
- **Transaction:** `damage` row (+ `D2627-…` number) and a `DAMAGE` movement (−qty).
- **Bulk write-off** from the expiry area: select expired lots → one transaction, one damage per lot.
- P&L: `cost_value − recovered_amount` under **Damages**.

### 12.2 Expenses

- Quick entry form optimized for speed: date (default today), category, amount, mode, paid to,
  reference, notes. "Save & new" keeps date and mode.
- Categories are user-editable (seeded: Rent, Electricity, Transport & Delivery, Packaging,
  Repairs & Maintenance, Shop Supplies, Phone & Internet, Bank & UPI Charges, Taxes & Fees,
  Miscellaneous), with `name_local`.
- **Asset purchases are not expenses** — the form warns when the category looks like equipment
  and links to the asset register.
- **Repeat last month** button for fixed monthly expenses (rent, internet): copies last month's
  entries of selected categories into a pre-filled draft list.

### 12.3 Employees & salary

- Employee master: name, phone, role, join/leave dates, monthly salary, active.
- **Monthly salary run:** choose month → grid of active employees with `monthly_salary`
  pre-filled → adjust (leave deductions, overtime rows, bonus rows) → pay date, mode → save as
  `salary_payment` rows. Salary is charged to `period_month` (the month worked), not the pay date.
- Advances: record as `kind = OTHER` with a note (a proper advance-recovery ledger is backlog).
- Salary register report: employee × month matrix.

---

## 13. Assets & depreciation

### 13.1 Asset register

Fields: name, category (Furniture / Equipment / Electronics / Vehicle / Other), purchase date,
in-use-from date, cost, vendor, method (WDV/SLM), yearly rate %, salvage value, notes.
Suggested defaults per category (editable): furniture 10% WDV, equipment/refrigeration 15% WDV,
computers 40% WDV, vehicles 15% WDV.

These are **book (management) depreciation** figures for the P&L. Income-tax depreciation
(block-of-assets, half-rate if used < 180 days) is the accountant's job; the asset register report
gives them the inputs.

### 13.2 Calculation

For each month *m* the asset is in use:

- `daysInUse` = days of *m* between `in_use_from` and `disposed_date` (inclusive).
- **WDV:** `yearly = bookValueAtFyStart × rate` (in the first FY, `bookValueAtFyStart = cost`).
- **SLM:** `yearly = (cost − salvage) × rate`.
- `charge = Round(yearly × daysInUse ÷ daysInFy)`.
- In the **last month of the FY** (or the disposal month), charge = FY-to-date entitlement −
  charges already posted in the FY — so each year's total is exact despite monthly rounding.
- Never depreciate below salvage: `charge = min(charge, opening − salvage)`.

```csharp
public static class Depreciation
{
    public static Money MonthlyCharge(AssetInfo a, YearMonth m, Money opening, Money fyStartValue,
                                      Money postedThisFy, int fyStartMonth)
    {
        var (mFrom, mTo) = m.Range();
        var start = Max(mFrom, a.InUseFrom);
        var end   = a.DisposedDate is { } d ? Min(mTo, d) : mTo;
        if (end < start) return Money.Zero;

        int fyStartYear = FiscalYear.StartYear(mFrom, fyStartMonth);
        int daysInFy    = FiscalYear.DaysIn(fyStartYear, fyStartMonth);
        decimal yearly  = a.Method == DepMethod.Wdv
            ? fyStartValue.Value * a.Rate.Value / 100m
            : (a.Cost - a.Salvage).Value * a.Rate.Value / 100m;

        Money charge;
        bool closesFy = m.IsLastMonthOfFy(fyStartMonth) || a.DisposedDate is { } dd && dd <= mTo;
        if (closesFy)
        {
            var (fyFrom, _) = FiscalYear.Range(fyStartYear, fyStartMonth);
            var usedFrom = Max(fyFrom, a.InUseFrom);
            int daysInFyUsed = end.DayNumber - usedFrom.DayNumber + 1;
            charge = new Money(yearly * daysInFyUsed / daysInFy) - postedThisFy;
        }
        else
        {
            charge = new Money(yearly * (end.DayNumber - start.DayNumber + 1) / daysInFy);
        }

        var cap = opening - a.Salvage;
        if (charge > cap) charge = cap;
        return charge < Money.Zero ? Money.Zero : charge;
    }
}
```

**Worked example** (test case): Visi-cooler, cost ₹45,000.000, WDV 15%, in use from 01-04-2026,
FY April–March.

| Period | Charge |
|---|---|
| FY 2026-27 yearly | 45,000 × 15% = ₹6,750.000 |
| April 2026 (30 days) | 6,750 × 30 ÷ 365 = **₹554.795** |
| May 2026 (31 days) | **₹573.288** |
| Apr–Feb total | ₹6,176.716 |
| March 2027 (closing month) | 6,750.000 − 6,176.716 = **₹573.284** |
| FY 2026-27 total | **₹6,750.000** exactly |
| FY 2027-28 | opening ₹38,250.000 → yearly ₹5,737.500 |

### 13.3 Posting job

Runs after login (background) and from Assets → "Post depreciation now":

```
target = last completed month (the month before the current business month)
for each asset with in_use_from ≤ end of target:
    next = month after its latest asset_depreciation row, or month(in_use_from)
    for m in next … min(target, month(disposed_date)):
        compute charge (§13.2); insert (asset, m, opening, charge, opening − charge)
all in one write transaction; UNIQUE(asset_id, period_month) makes re-runs harmless
```

Months inside a locked period (`books.locked_until`) are not posted; the job reports them instead.

### 13.4 Disposal

Asset → Dispose: date and proceeds. The service first posts depreciation up to and including the
disposal month (partial), then sets `disposed_date`/`disposal_value`. P&L shows
`proceeds − book value` under **Asset disposal** (gain or loss).

### 13.5 Reports

Asset register (cost, accumulated depreciation, book value as of a date) and per-asset schedule
(posted months + projected future months computed on the fly, not stored).

---

## 14. Reports, dashboard & expiry area

### 14.1 Profit & Loss

Source: `v_pnl_entry` (migration 002). Periods: this month, last month, this FY, last FY, custom
range; optional comparison with the previous equivalent period.

```csharp
public Task<PnlStatement> GetAsync(DateOnly from, DateOnly to) =>
    db.ReadAsync(c =>
    {
        var rows = c.Query<PnlRow>("""
            SELECT section AS Section, head_code AS HeadCode, ref_id AS RefId, SUM(amount) AS Amount
            FROM v_pnl_entry
            WHERE entry_date BETWEEN @from AND @to
            GROUP BY section, head_code, ref_id
            """, new { from, to }).ToList();
        var categories = c.Query<(long Id, string Name, string? NameLocal)>(
            "SELECT id, name, name_local FROM expense_category").ToDictionary(x => x.Id);
        return PnlStatement.Build(rows, categories);   // labels via Pnl_Head_* / LocalizedText.Pick
    });
```

Statement layout:

```
Income
  Sales (net of GST)                   SALES
  Less: Sales returns                  SALES_RETURNS
  Round off                            ROUND_OFF
= Net sales
Cost of goods sold                     COGS (sales − returns)
= GROSS PROFIT            (and gross margin % of net sales)
Operating expenses
  Damages & expiry                     DAMAGES
  Stock adjustments                    STOCK_ADJUSTMENT
  Salaries                             SALARIES
  Depreciation                         DEPRECIATION
  <each expense category>              EXPENSE_CATEGORY / ref_id
= Total expenses
Other income
  Asset disposal gain/(loss)           ASSET_DISPOSAL
= NET PROFIT              (and net margin %)
```

**Monthly/yearly matrix:** same query with `strftime('%Y-%m', entry_date) AS Month` added to the
select and group-by → months as columns, heads as rows, FY total column. Months use
`DisplayFormatter.MonthYear` (localized month names).

**Explain it in the UI:** a help note on the P&L page — shop owners often expect
"profit = sales − purchases". Purchases go into stock; only the **cost of what was sold** reaches
the P&L. The Stock value report shows where the rest of the purchase money is.

Custom ranges: salaries and depreciation are dated at month-end, so a range that cuts a month in
half includes them only if it contains the month's last day — shown as a note under the statement.

### 14.2 Dashboard

Loads after the shell renders; each tile queries independently; refreshes on
`SaleCompletedMessage` (debounced 5 s) and on navigation.

| Tile | Query basis |
|---|---|
| Today: sales, bills, average bill, gross margin | `v_daily_sales` for today + `cost_total` |
| This month vs last month (same day count) | `sale` by date range |
| Payment mode split today | `sale_payment` joined to today's completed sales |
| Sales trend (last 30 days) | `v_daily_sales` → LiveCharts2 line chart |
| Top 10 products (30 days) by revenue and by margin | `sale_line` joined to `sale` by date |
| Expiry: expired / ≤ 7 days / ≤ alert window | `v_expiry_alert` counts by bucket |
| Low stock count | `v_product_stock` where stock ≤ reorder level |
| Stock value at cost | Σ `qty_on_hand × unit_cost` of active lots |
| Payables | Σ `v_vendor_balance` |
| Backup status | last backup time, last USB export |

Each tile is clickable and opens the matching report with the same filter.

### 14.3 Expiry area

- Page with three tabs: **Expired**, **Critical (≤ 7 days)**, **Soon (≤ alert window)**, from
  `v_expiry_alert` (`qty_on_hand > 0`, active lots). Alert window cascades product → category →
  parent category → `expiry.default_alert_days` → 30.
- Columns: product (+ local), lot/batch, expiry, days left, qty, value at cost, vendor.
- Actions: **write off** (damage, §12.1) · **mark down** (lot price change, audited) ·
  **print list for vendor** (return/replacement negotiation) · **block lot**.
- Navigation badge = expired + critical count; optional popup at first login of the day
  (`expiry.popup_on_login`).

### 14.4 Other reports

| Report | Notes |
|---|---|
| Sales register | Bills by date range, mode, status; totals |
| Item-wise sales | Qty, revenue, cost, margin per product; category subtotals |
| Category-wise sales & margin | For range; comparison with previous period |
| Hourly sales heatmap | Bills and revenue by weekday × hour (staffing) |
| Stock value | At cost / selling price / MRP, by category |
| Stock ledger | Per product/lot (§10.1) |
| Low stock / reorder | Stock ≤ reorder level, with 30-day average daily sales |
| Slow movers | Stock with no sale in N days, value tied up |
| Expiry | §14.3 in report form |
| Damages | By reason, category, vendor |
| Expenses | By category, month |
| Salary register | Employee × month |
| Asset register & depreciation | §13.5 |
| Vendor ledger / payables | §9.6 |
| Purchase register | By vendor, date, with tax |
| **GST summary (for the accountant)** | Outward supplies by GST rate and by HSN (from `sale_line` snapshots `tax_rate_pct`, `hsn_code`) minus returns; inward supplies by vendor GSTIN and rate. Not a filing tool. |
| Audit log | Filter by entity/action/date |

Every report: filter panel · virtualized grid · totals row · **Export to Excel** (ClosedXML,
numbers as numbers with 3-decimal format, both name columns) · **Print A4** (§15.5).

---

## 15. Printing

### 15.1 Printer settings

Settings → Printers: pick Windows printer queues for **receipt**, **label** and **A4**; receipt
width 58 mm (384 dots) or 80 mm (576 dots); receipt mode `RASTER` (default) or `TEXT`; label size;
"Print test page" for each. All printers must be installed with their Windows driver (or the
"Generic / Text Only" driver) so they appear as queues.

### 15.2 Raw printing through the spooler

```csharp
public static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DOC_INFO_1
    {
        public string? pDocName;
        public string? pOutputFile;
        public string? pDatatype;
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOC_INFO_1 di);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    public static void Send(string printerName, byte[] data, string docName = "Grocery ERP")
    {
        if (!OpenPrinter(printerName, out var h, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!StartDocPrinter(h, 1, new DOC_INFO_1 { pDocName = docName, pDatatype = "RAW" }))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!StartPagePrinter(h)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!WritePrinter(h, data, data.Length, out var written) || written != data.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                EndPagePrinter(h);
            }
            finally { EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }
}
```

Print jobs run on a background task through a single-consumer queue (`Channel<PrintJob>`), so the
POS never waits on the spooler. Failures surface as a Snackbar with a retry action.

### 15.3 Receipts (ESC/POS)

**Builder:**

```csharp
public sealed class EscPos
{
    private readonly List<byte> _b = new();

    public EscPos Init()               => Add(0x1B, 0x40);                          // ESC @
    public EscPos Align(int a)         => Add(0x1B, 0x61, (byte)a);                 // 0 L, 1 C, 2 R
    public EscPos Bold(bool on)        => Add(0x1B, 0x45, (byte)(on ? 1 : 0));      // ESC E n
    public EscPos Text(string ascii)   => Add(Encoding.ASCII.GetBytes(ascii + "\n"));
    public EscPos Feed(int lines)      => Add(0x1B, 0x64, (byte)lines);             // ESC d n
    public EscPos Cut()                => Add(0x1D, 0x56, 0x42, 0x00);              // GS V 66 0
    public EscPos OpenDrawer()         => Add(0x1B, 0x70, 0x00, 0x19, 0xFA);        // ESC p 0 25 250

    /// GS v 0 raster, sent in bands so small printer buffers don't overflow.
    public EscPos Raster(MonoBitmap img, int bandRows = 128)
    {
        for (int y = 0; y < img.Height; y += bandRows)
        {
            int rows = Math.Min(bandRows, img.Height - y);
            Add(0x1D, 0x76, 0x30, 0x00,
                (byte)(img.WidthBytes & 0xFF), (byte)(img.WidthBytes >> 8),
                (byte)(rows & 0xFF), (byte)(rows >> 8));
            Add(img.Bits.AsSpan(y * img.WidthBytes, rows * img.WidthBytes).ToArray());
        }
        return this;
    }

    public byte[] ToArray() => _b.ToArray();
    private EscPos Add(params byte[] bytes) { _b.AddRange(bytes); return this; }
}
```

**Raster pipeline** (works for every language):

1. Build a `ReceiptModel` from the completed sale snapshots and the print culture (§5.9).
2. Create `ReceiptVisual` (a WPF `UserControl` bound to the model), width = printer dots
   (576 or 384 px), body font 22–24 px, totals 32 px, `TextOptions.TextFormattingMode="Display"`,
   and the `NumberSubstitution` setters from §5.5 so digits print as `0–9` in both languages.
3. `Measure`/`Arrange` at that width → height = `DesiredSize.Height`.
4. Render with `RenderTargetBitmap(width, height, 96, 96, Pbgra32)` on the UI thread (≈ 10–30 ms).
5. Convert to 1-bit:

```csharp
public static MonoBitmap ToMono(BitmapSource src)
{
    int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4, wb = (w + 7) / 8;
    var px = new byte[stride * h];
    src.CopyPixels(px, stride, 0);
    var bits = new byte[wb * h];
    for (int y = 0; y < h; y++)
    for (int x = 0; x < w; x++)
    {
        int i = y * stride + x * 4;
        int a = px[i + 3];
        // premultiplied BGRA composited over white
        int lum = ((px[i + 2] + 255 - a) * 299 + (px[i + 1] + 255 - a) * 587 + (px[i] + 255 - a) * 114) / 1000;
        if (lum < 128) bits[y * wb + (x >> 3)] |= (byte)(0x80 >> (x & 7));
    }
    return new MonoBitmap(w, h, wb, bits);
}
```

6. `new EscPos().Init().Raster(mono).Feed(3).Cut()` (+ `OpenDrawer()` when paid in cash) →
   `RawPrinter.Send`.

The invoice-number barcode at the bottom is drawn into the same visual (Code 128 via ZXing.Net),
with bar widths snapped to whole pixels so it scans reliably.

**TEXT mode** (English only, faster on slow printers): fixed-width columns (48 chars at 80 mm
Font A, 32 at 58 mm), "Rs" instead of `₹`, same content.

### 15.4 Labels (TSPL)

```
SIZE 50 mm, 25 mm
GAP 2 mm, 0 mm
DIRECTION 1
CLS
TEXT 16,8,"2",0,1,1,"Tata Salt 1 kg"
TEXT 16,36,"2",0,1,1,"MRP Rs 30.000   Our Rs 28.000"
BARCODE 16,64,"EAN13",70,1,0,2,4,"200000001234"
PRINT 1,1
```

- EAN-13 is sent as the 12-digit body; the printer adds the check digit (verify on your model;
  some accept 13 digits).
- `BARCODE` narrow = 2 dots at 203 dpi keeps the symbol within 50 mm and well above minimum size.
- Gujarati names: render the text with WPF into a mono bitmap (as §15.3) and send with
  `BITMAP x,y,widthBytes,height,0,<data>`. Check the model's bit polarity (many TSPL printers treat
  0 as a black dot — invert if the test label prints negative).
- Templates (price label, barcode + price, shelf-edge) are defined in code with a preview rendered
  in WPF from the same layout values.
- Fallback for non-TSPL printers: print the WPF label visual through the Windows driver
  (`PrintDialog.PrintVisual`) — slower but universal.

### 15.5 A4 documents

- Built as `FixedDocument` from WPF visuals (with the §5.5 `NumberSubstitution` setters): A4 portrait (793.7 × 1122.5 DIPs at 96 DPI), 12 mm
  margins, header (store details), paginated table rows (measure rows, break when the page is full,
  repeat column headers), footer (page X of Y, printed at).
- Used for: B2B tax invoices, purchase registers, P&L, stock reports, count sheets.
- Print via `PrintDialog.PrintDocument(doc.DocumentPaginator, title)`; **PDF** by selecting
  "Microsoft Print to PDF" (the app can pre-select that queue for a "Save as PDF" button).
- Preview with `DocumentViewer`.

---

## 16. Backup, restore & integrity

### 16.1 When backups happen

| Trigger | Reason tag |
|---|---|
| App close | `close` |
| First launch of the day, if no backup today | `daily` |
| Before migrations | `pre-migration-vN` |
| Before restore | `pre-restore` |
| Before applying an update | `pre-update` |
| Manual (Settings → Backup now) | `manual` |

### 16.2 Backup set format

Each backup is **one `.zip` file** (built with `System.IO.Compression` — no extra package):

```
GroceryErp_2026-09-22_101500_daily.zip
├── grocery.db        consistent snapshot (VACUUM INTO)
└── manifest.json     what it is, and how to prove it is intact
```

```json
{
  "format": 1,
  "app": "GroceryErp",
  "appVersion": "1.2.0",
  "schemaVersion": 3,
  "createdAt": "2026-09-22T10:15:00",
  "reason": "daily",
  "machine": "SHOP-PC",
  "database": {
    "file": "grocery.db",
    "bytes": 147062784,
    "sha256": "9f2c…e41a",
    "quickCheck": "ok"
  },
  "summary": {
    "products": 20000,
    "bills": 639100,
    "lastInvoiceNo": "S2627-012345",
    "lastSaleDate": "2026-09-22"
  }
}
```

The summary lets the owner recognize the right backup at restore time; the hash proves the
database inside the zip is exactly what was snapshotted. Measured on the 5-year test dataset:
snapshot 4 s, SHA-256 0.2 s, zip 0.9 s, **147 MB → 37 MB** (about 4× smaller, which matters for USB copies).

### 16.3 Creating and verifying

```csharp
public sealed record BackupManifest(int Format, string App, string AppVersion, long SchemaVersion,
    string CreatedAt, string Reason, string Machine, BackupDbInfo Database, BackupSummary Summary);
public sealed record BackupDbInfo(string File, long Bytes, string Sha256, string QuickCheck);
public sealed record BackupSummary(long Products, long Bills, string? LastInvoiceNo, string? LastSaleDate);

public BackupInfo Create(string reason)
{
    var stamp = clock.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
    var work = Path.Combine(paths.TempDir, $"backup-{stamp}");
    Directory.CreateDirectory(work);
    Directory.CreateDirectory(BackupDir);
    try
    {
        var snapshot = Path.Combine(work, "grocery.db");
        using (var c = factory.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "VACUUM INTO $path";          // consistent copy while the app keeps running
            cmd.Parameters.AddWithValue("$path", snapshot);
            cmd.ExecuteNonQuery();
        }

        var (quickCheck, schemaVersion, summary) = Inspect(snapshot);   // read-only, Pooling = false
        if (quickCheck != "ok") throw new DomainException(ErrorCode.BackupInvalid, quickCheck);

        var manifest = new BackupManifest(1, "GroceryErp", appInfo.Version, schemaVersion,
            clock.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), reason,
            Environment.MachineName,
            new BackupDbInfo("grocery.db", new FileInfo(snapshot).Length, Sha256Hex(snapshot), quickCheck),
            summary);

        var final   = Path.Combine(BackupDir, $"GroceryErp_{stamp}_{reason}.zip");
        var partial = final + ".partial";                  // never listed or restored as a backup
        using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(snapshot, "grocery.db", CompressionLevel.Fastest);
            using var json = zip.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
            JsonSerializer.Serialize(json, manifest, ManifestJson.Options);   // camelCase, indented
        }
        Verify(partial);                                    // re-read zip: manifest parses, hash matches
        File.Move(partial, final);                          // only verified backups get the final name
        Rotate();
        return new BackupInfo(final, new FileInfo(final).Length, manifest);
    }
    finally
    {
        TryDeleteDirectory(work);
    }
}

private static string Sha256Hex(string file)
{
    using var fs = File.OpenRead(file);
    return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
}
```

`Verify(zipPath)` opens the zip, parses `manifest.json`, streams the `grocery.db` entry through
SHA-256 and compares with the manifest. It runs after every backup and after every copy to USB,
so a bad disk or a pulled USB stick is caught immediately, not on the day a restore is needed.

**Rotation:** keep the newest `backup.keep` (30) sets, plus the last set of each month for
12 months, plus every `pre-*` set for 90 days. `*.partial` files older than a day are deleted.

### 16.4 Off-machine copies

- Settings → **Export to USB**: lists removable drives (`DriveInfo.GetDrives()` where
  `DriveType == Removable`), copies the newest set plus the monthly ones into
  `GroceryErp-Backups\`, **verifies each copied file** (§16.3), writes `README.txt` (how to
  restore), records `backup.last_usb_export`.
- Title-bar indicator turns amber after 7 days without a USB export and red after 14.
- Recommend two USB drives used alternately, one kept at home.

### 16.5 Restore

Restore never touches the live database until the backup has been fully checked in a temporary
location, and the replaced database is kept until the restored one has started successfully.

**1. Inspect** (no changes yet):

```csharp
public RestoreCandidate Inspect(string zipPath)
{
    using var zip = ZipFile.OpenRead(zipPath);
    var manifest = ReadManifest(zip.GetEntry("manifest.json")
        ?? throw new DomainException(ErrorCode.BackupInvalid, "manifest missing"));
    if (manifest.Format != 1 || manifest.App != "GroceryErp")
        throw new DomainException(ErrorCode.BackupInvalid, "unknown format");
    if (manifest.SchemaVersion > MigrationRunner.LatestVersion)
        throw new DomainException(ErrorCode.DatabaseNewerThanApp, manifest.SchemaVersion);

    var staging = Path.Combine(paths.TempDir, $"restore-{Guid.NewGuid():N}");
    Directory.CreateDirectory(staging);
    var staged = Path.Combine(staging, "grocery.db");
    (zip.GetEntry("grocery.db") ?? throw new DomainException(ErrorCode.BackupInvalid, "database missing"))
        .ExtractToFile(staged);                        // fixed name — never extract by entry path

    if (!Sha256Hex(staged).Equals(manifest.Database.Sha256, StringComparison.Ordinal))
        throw new DomainException(ErrorCode.BackupInvalid, "checksum mismatch");
    CheckDatabase(staged, manifest);   // integrity_check = ok, foreign_key_check empty,
                                       // user_version = manifest.SchemaVersion, required tables exist
    return new RestoreCandidate(zipPath, staged, manifest);
}
```

**2. Confirm.** Show the manifest summary (backup date, reason, last bill number and date, product
and bill counts, app version) next to the same figures for the current database. The user types
`RESTORE` to continue.

**3. Apply:**

1. Backup the current database (`pre-restore`).
2. Stop background jobs, run a `wal_checkpoint(TRUNCATE)` that must report no busy readers,
   then `SqliteConnection.ClearAllPools()`.
3. Write `db/restore.pending.json` with stage `preparing` before the first rename.
4. Move `db/grocery.db` (+ `-wal`, `-shm`) to `db/replaced-<stamp>/`.
5. Move the staged file to `db/grocery.db` (same volume → atomic rename), then atomically
   change the marker stage to `applied`.
6. Restart the app.

**4. Finish on the next start** (startup step 7): migrations run (an older backup is upgraded),
the startup checks pass → write a `RESTORE` audit entry, delete the marker; the `replaced-*` folder
is kept 7 days, then purged. If startup fails while the marker exists, the app offers
**Undo restore**, which moves the replaced database back.
Before opening SQLite, a `preparing` marker is rolled back to the original database. A marker
whose original database was never moved is simply removed. Pending replaced data is excluded
from the seven-day purge.

### 16.6 Integrity & maintenance

| Check | When |
|---|---|
| Lot cache vs ledger (`v_lot_cache_mismatch`) | Every startup (fast) |
| FTS consistency: `INSERT INTO product_fts(product_fts) VALUES('integrity-check')` | Weekly |
| `PRAGMA integrity_check` | Weekly, background |
| `PRAGMA foreign_key_check` | Weekly, background |
| `PRAGMA optimize` | On close |
| `PRAGMA wal_checkpoint(TRUNCATE)` | On close |
| Free disk space | Startup and before backups |

Any failure → log, banner in the shell, and a "Restore from backup" suggestion if structural.

---

## 17. Performance engineering

### 17.1 Targets (5-year synthetic dataset, mid-range PC with SSD)

| Operation | Target |
|---|---|
| Cold start → login screen | < 2 s |
| Scan → line visible on bill | < 50 ms |
| Product search keystroke → results visible | < 100 ms (query < 30 ms) |
| Complete bill (commit) | < 100 ms |
| Receipt starts printing | < 1 s after completion |
| Sales history first page | < 200 ms |
| P&L month / FY | < 200 ms / < 500 ms |
| Dashboard fully loaded | < 1 s |
| Daily backup of ~1 GB DB | background, < 60 s |

### 17.2 Synthetic dataset (`tools/GroceryErp.DataGen`)

| Entity | Volume | Shape |
|---|---|---|
| Categories / brands | 60 / 300 | 3-level tree |
| Products | 20,000 | 30% with `name_local`, valid 890… EANs, 5% loose |
| Vendors | 150 | |
| Purchases | ~26,000 | 2–40 lines each |
| Lots | ~150,000 | realistic expiry spread |
| Bills | ~640,000 | ~350/day, seasonal (Diwali peak), 5 years |
| Bill lines | ~3.8 M | Pareto: 20% of products = 80% of lines |
| Expenses / salaries / assets | realistic monthly patterns | |

Insert through the real services' SQL (so triggers run) in transactions of ~50k rows with reused
prepared commands. Expect a multi-minute run and a database on the order of 1 GB.

### 17.3 Techniques

- **Queries:** every hot query has a covering or selective index; tests assert query plans
  (`EXPLAIN QUERY PLAN` contains `USING INDEX` / `USING COVERING INDEX`, never `SCAN sale_line`).
- **Aggregates** use the snapshot totals on `sale`/`sale_return` (no line scans for P&L).
- **Search:** FTS5 + `LIMIT 100` → in-memory popularity re-rank → top 30. The popularity map
  (product → units sold in 90 days) is built in the background at startup and refreshed hourly.
- **Startup:** ReadyToRun; modules' pages are created lazily on first navigation; dashboard data
  loads after first render; background jobs start after the shell is visible.
- **UI:** virtualization rules (§4.5); frozen brushes; no drop shadows/blur on large lists; Mica
  off by default; avoid `ObservableCollection` churn — replace the whole collection for reloads.
- **Writes:** one transaction per user action; `synchronous=FULL` costs ~1–5 ms per commit on SSD
  — acceptable at POS rates.

### 17.4 Benchmarks

BenchmarkDotNet projects for `PricingEngine.Price` (50-line bill), `ScanResolver.Resolve`,
`AllocateFefo`, FTS search, P&L FY query — run against the generated DB before each release;
results stored in `docs/perf/<version>.md` and compared with the previous release (> 20%
regression blocks the release).

---

## 18. Testing strategy

### 18.1 Layers

| Layer | Project | Runs | Scope |
|---|---|---|---|
| Unit | Core.Tests | every build | numerics, tax, allocation, pricing, EAN/GS1, depreciation, fiscal year, i18n helpers |
| Property | Core.Tests | every build | invariants over random inputs |
| Integration | Integration.Tests | every build | real schema on temp files: services, triggers, views, migrations |
| Snapshot | Integration.Tests | every build | receipt bytes, receipt images, TSPL output, report models |
| Architecture | Architecture.Tests | every build | module boundaries, banned types |
| UI smoke | UI.Tests | before release | FlaUI end-to-end paths |
| Performance | Benchmarks | before release | §17 |

### 18.2 Test database fixture

```csharp
public sealed class TempDb : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"erp-{Guid.NewGuid():N}.db");
    public SqliteConnectionFactory Factory { get; }

    public TempDb()
    {
        Factory = new SqliteConnectionFactory(new AppPaths(new TestBoot(Path)));
        new MigrationRunner(Factory, NullBackup.Instance, NullLogger<MigrationRunner>.Instance).Run();
        Seed.Minimal(Factory);                // store settings, one category, units, tax rates
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" }) File.Delete(f);
    }
}
```

Real files (not `:memory:`) so WAL, pooling and file-level behaviour match production.

### 18.3 Property-based tests (FsCheck)

- `Allocate(total, weights)` sums to `total` exactly; each share within 0.001 of the exact
  proportion; zero weights get zero.
- `FromInclusive(g, r)`: `taxable + tax == g`; `taxable ≤ g`.
- `SplitIntraState(t)`: `cgst + sgst == t`; `|cgst − sgst| ≤ 0.001`.
- `PricingEngine.Price`: `GrandTotal == Taxable + Tax + RoundOff`; every line
  `Total == Taxable + Tax`; Σ line discounts == Σ line discounts entered + bill discount.
- `Ean.Internal(p, s)` is always valid and round-trips `s`.
- `InputNormalizer` maps every Unicode `Nd` digit to its ASCII value.

### 18.4 Golden scenario ("a month in the life")

One integration test builds a whole month through the services: opening stock, three purchases
(one with free goods and other charges, one composition-style), ~40 bills (inclusive and exclusive
tax, line and bill discounts, split payments, a weighed item, a two-lot split), a hold/resume,
a void, a partial return (one resellable, one not), damages, a stock count with shortage, expenses,
salaries, an asset bought mid-month, depreciation posting. Assertions: every stock figure, every
P&L head, the day-end summary and the vendor balance — against numbers computed by hand in a
spreadsheet checked into `tests/golden/`. The §3.6 and §13.2 worked examples are part of it.

### 18.5 Database rule tests

One test per trigger/constraint: negative stock blocked (and allowed with the setting), ledger
UPDATE/DELETE blocked, completed sale edit/delete blocked, posted purchase lines locked, lot/product
mismatch rejected, selling above MRP rejected, invalid date rejected, duplicate vendor bill
rejected — each also asserting the `ErrorCode` produced by `SqliteErrorMapper`.

### 18.6 Duplicate-submission and backup tests

- For every `OpType`: calling the service twice with the same `OpId` creates one document, one
  set of movements and returns `IsReplay = true` the second time; two parallel calls (tasks
  racing on the write gate) produce the same result.
- A failed attempt (validation error) followed by a corrected retry with the same `OpId` succeeds.
- Operation purge removes only rows older than 30 days.
- Backup set: manifest fields and hash match the snapshot; a zip with a modified `grocery.db`,
  a missing manifest, a truncated file, or a `schemaVersion` newer than the app is rejected with
  `BackupInvalid` / `DatabaseNewerThanApp` before anything is replaced.
- Restore round trip: backup → change data → restore → data equals the backup; killing the app
  between steps 3 and 6 leaves either the old or the new database usable (marker + undo).

### 18.7 Migration tests

- Empty file → latest: `user_version` correct, `PRAGMA foreign_key_check` clean, all views
  compile (`SELECT * FROM v LIMIT 0`).
- A v1 database **with data** (checked-in fixture) → latest: row counts unchanged, P&L totals
  unchanged except the new stock-adjustment head.
- Newer-than-app database → `DatabaseNewerThanApp`.

### 18.8 i18n tests

```csharp
[Theory]
[MemberData(nameof(AllResourceManagers))]
public void Every_key_is_translated(ResourceManager rm)
{
    var neutral = rm.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
    foreach (var lang in new[] { "gu" })
    {
        var set = rm.GetResourceSet(CultureInfo.GetCultureInfo(lang), true, false);
        foreach (DictionaryEntry e in neutral)
            set?.GetString((string)e.Key).ShouldNotBeNullOrWhiteSpace($"{e.Key} missing in {lang}");
    }
}
```

**Numbers rule tests:**

```csharp
private static bool HasGujaratiDigit(string s) => s.Any(c => c is >= '\u0AE6' and <= '\u0AEF');

[Theory]
[MemberData(nameof(AllResourceManagers))]
public void Gujarati_resources_contain_no_Gujarati_digits(ResourceManager rm)
{
    var set = rm.GetResourceSet(CultureInfo.GetCultureInfo("gu"), true, false);
    foreach (DictionaryEntry e in set!)
        HasGujaratiDigit((string)e.Value!).ShouldBeFalse($"{e.Key} contains Gujarati digits");
}

[Fact]
public void Formatting_uses_English_digits_in_Gujarati_ui()
{
    TranslationSource.Instance.UiCulture = CultureInfo.GetCultureInfo("gu-IN");   // non-parallel collection
    var f = new DisplayFormatter();
    f.Money(new Money(1234567.891m)).ShouldBe("₹12,34,567.891");
    f.MonthYear(2026, 9).ShouldEndWith(" 2026");
    TranslationSource.Instance.Plural("Sales_Pos_Items", 3).ShouldContain("3");
}
```

Also: every `{l:Tr Key}` found in `*.xaml` (regex over the source tree) exists in a neutral file;
placeholder sets (`{0}`, `{1}`…) are identical in English and Gujarati; `Plural` rules for en and gu;
`InputNormalizer("૮૯૦૧")` = `"8901"`; saving a product named "ચા ૨૫૦ ગ્રામ" stores "ચા 250 ગ્રામ";
FTS finds products by Gujarati substrings and finds the same rows for "૨૫૦" and "250"; a Gujarati
receipt model and TSPL label output contain no Gujarati digits; the FlaUI smoke run in Gujarati walks
every page's visible text and asserts no Gujarati digit and no `⟦`; pseudo mode asserts no `⟦`.

### 18.9 Architecture tests

- Modules reference other modules only via `*.Contracts` namespaces.
- `GroceryErp.Core` references only the base class library — no WPF, no SQLite, no packages
  (this is the test that catches a stray `IServiceCollection` or `Dapper` in Core):

```csharp
[Fact]
public void Core_references_only_the_base_class_library()
{
    var foreign = typeof(Money).Assembly.GetReferencedAssemblies()
        .Select(a => a.Name!)
        .Where(n => n != "System" && n != "netstandard" && !n.StartsWith("System.", StringComparison.Ordinal))
        .ToList();
    foreign.ShouldBeEmpty();
}
```

- Only `GroceryErp.UI.Common`, `GroceryErp.App` and module assemblies reference
  `Microsoft.Extensions.DependencyInjection.Abstractions`; Data, Localization and Printing do not.
- No `System.Double`/`System.Single` members in `*.Domain` and `GroceryErp.Core` public APIs.
- ViewModels don't reference `Microsoft.Data.Sqlite`.

### 18.10 UI smoke tests (FlaUI)

Launch with a temp data root → first-run wizard → create product → purchase it → sell it with
cash → verify dashboard shows the sale → switch to Gujarati → verify shell labels changed →
lock/unlock → close (backup created).

### 18.11 Manual test sheet (before every release)

Real scanner (1D, 2D, scale label), real receipt printer (English and Gujarati receipts, checking every number prints in English digits),
real label printer, cash drawer kick, power-pull test mid-bill (restart → draft resumes),
restore from USB on a second PC, 125%/150% display scaling, 1366×768 screen.

---

## 19. Packaging, installation & updates

### 19.1 Entry point for Velopack

```csharp
// GroceryErp.App/Program.cs
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();        // must run first: install/update/uninstall hooks
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
```

```xml
<!-- GroceryErp.App.csproj: stop WPF generating its own Main -->
<PropertyGroup>
  <StartupObject>GroceryErp.App.Program</StartupObject>
</PropertyGroup>
<ItemGroup>
  <ApplicationDefinition Remove="App.xaml" />
  <Page Include="App.xaml" />
</ItemGroup>
```

### 19.2 Build a release

```powershell
dotnet publish src/GroceryErp.App -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true -p:Version=1.2.0 -o publish
vpk pack --packId GroceryErp --packVersion 1.2.0 --packDir publish --mainExe GroceryErp.exe
```

Output (`Releases/`): `GroceryErp-win-Setup.exe`, full and delta packages, release feed files.

### 19.3 Install

Run `Setup.exe` on the shop PC (no admin needed): installs per-user, creates Start-menu and desktop
shortcuts, launches the app → first-run wizard. Unsigned installers show a SmartScreen prompt
("More info → Run anyway"); signing the installer removes it.

### 19.4 Offline updates

1. Copy the `Releases/` folder to a USB stick.
2. In the app: Settings → Updates → "Check folder…" (remembers the last path).

```csharp
var mgr = new UpdateManager(folderPath);
var update = await mgr.CheckForUpdatesAsync();
if (update is null) { messages.Info("Settings_Update_None"); return; }
backup.Create("pre-update");
await mgr.DownloadUpdatesAsync(update);        // copies from the folder
mgr.ApplyUpdatesAndRestart(update);            // restart → migrations run at startup
```

3. Release notes (per language) are shown after restart from an embedded `CHANGELOG.<lang>.md`.
The selected update folder is remembered after every successful folder check, including when no
newer release is found. Checking and the verified pre-update backup run off the WPF thread.

### 19.5 Versioning

App: SemVer. Database: `user_version`. Each release note lists the schema version it brings. The
app refuses to open a DB newer than it understands (protects against running an old installer on
a newer DB).

---

## 20. Store go-live checklist

**PC**
- [ ] Windows fully updated; time zone (UTC+05:30) Chennai, Kolkata, Mumbai, New Delhi; date/time correct
- [ ] Sleep and hibernate disabled while plugged in; UPS connected and tested
- [ ] Data folder not under OneDrive; BitLocker on (if Pro)
- [ ] Gujarati Phonetic keyboard installed (if Gujarati names will be typed)
- [ ] Windows region format checked: app still shows English digits (NumberSubstitution pinned)

**Hardware**
- [ ] Scanner programmed (§8.1); test sheet of EAN-13, UPC-A, scale label, 2D code scans correctly
- [ ] Receipt printer: test page in each print language; cutter works; drawer opens
- [ ] Label printer: calibrated to label size; test label scans from 30 cm

**Data**
- [ ] Store details, GSTIN, tax mode, FY start confirmed with the accountant
- [ ] Tax rates and HSN codes verified with the accountant
- [ ] Products imported; top 200 sellers have barcodes and local names
- [ ] Opening stock imported and spot-checked (10 random products counted)
- [ ] Vendors with opening balances; employees; assets with in-use dates

**Safety**
- [ ] First backup taken; USB export done; restore tested on another PC
- [ ] Owner knows the password and has it written down somewhere safe
- [ ] Owner walkthrough: billing, hold/resume, void, return, receipt reprint, expiry area, backup to USB

**Parallel run**
- [ ] 1–2 weeks alongside the existing billing method; daily comparison of totals; then switch

---

## 21. Delivery plan

Effort is for one full-time developer; adjust for part-time work. i18n is built into every phase
from the start (keys + English text immediately; Gujarati translation batches at the end of
each phase).

### 21.1 Phases

| # | Phase | Weeks | Scope | Exit criteria |
|---|---|---|---|---|
| 0 | Skeleton | 1 | Solution, props, CPM, analyzers, CI; shell with NavigationView; `TranslationSource` + `{l:Tr}` with live English ↔ Gujarati switching on a demo page, numbers pinned to English digits; Serilog | App starts, switches language live, tests run in CI |
| 1 | Foundations | 2–3 | Paths, connection factory, DbExecutor, migrations 001–003, operation guard, Dapper handlers, numerics + property tests, errors/mapping, clock + FY, doc numbers, settings, audit, backup sets with manifest + verified restore, first-run wizard, login/lock | Wizard → login → empty shell; backup → restore round trip passes; all Core tests green |
| 2 | Catalog & barcodes | 2–3 | Masters, product CRUD/archive, product list, FTS search, EAN/internal barcodes, scale rules, Excel import, label printing (TSPL) | 20k-product import < 1 min; search < 30 ms; labels scan |
| 3 | Stock-in | 2 | Vendors, purchase entry, landed cost, posting, cancel, vendor payments, lots screen, stock ledger, opening stock import, stock count, adjustments | Golden purchases + counts reconcile |
| 4 | POS | 3–4 | Scan pipeline + burst detector, FEFO, pricing engine, payment dialog, completion, receipt raster printing (English/Gujarati), drawer, hold/resume, void, returns, reprint, sales history, day-end | Scan→line < 50 ms; power-pull test passes; receipts correct in both languages with English digits |
| 5 | Money out | 2 | Damages, expiry area actions, expenses, salary run, asset register, depreciation job, disposal | Depreciation worked example exact; damages in P&L |
| 6 | Insight | 2 | P&L (all periods + matrix), dashboard, all reports, Excel export, A4 printing | Golden scenario P&L matches spreadsheet; FY P&L < 500 ms |
| 7 | Hardening & release | 2 | Data generator + perf pass, FlaUI smoke, pseudo-loc pass, full Gujarati translation review by a native speaker, integrity jobs, Velopack installer, USB updates, go-live checklist, pilot | All targets met on 5-year dataset; pilot store live |

**Total: roughly 16–19 weeks.**

### 21.1.1 Implementation status

Last updated: **5 October 2026**

| Phase | Status | Evidence / remaining work |
|---|---|---|
| 0 — Skeleton | **Implemented locally; hosted CI pending** | Solution and all planned project boundaries created; centralized package pins and analyzers enabled; WPF UI `NavigationView` shell added; `{l:Tr}` switches English ↔ Gujarati at runtime; numeric display remains fixed to `en-IN`; Serilog rolling files configured; Release build has zero warnings; 6 tests pass, including a real STA WPF-window smoke test and Core BCL-only architecture test. GitHub Actions workflow is present, but its first hosted run requires publishing this workspace to a GitHub repository. Gujarati strings are machine-drafted and marked `MT – needs review`. |
| 1 — Foundations | **Implemented; external review remains** | The first-run wizard now keeps the active form beside its step rail, validates visited steps before completion, seeds Gujarati names for units/expense categories, creates a usable optional weight-label rule, and offers printer test actions and data-root selection. Settings provides manual backup, verified USB export, data-root move, password change and a current-vs-backup restore comparison with typed confirmation. The shell shows backup/USB health and blocks a second instance. Numeric property tests, localization lookup tests, data-move and restore UI smoke tests pass. Native Gujarati review, physical printer/USB checks and complete public XML documentation remain open below. |
| 2 — Catalog & barcodes | **Software workflows implemented; external sign-off remains** | Product/master CRUD, audit, FTS/prefix search, list filters, direct product lots/ledger views, category tree drag-reparent, EAN/internal/scale/GS1 handling, Excel dry-run/import with row selection and error export, bulk category/tax changes, multi-product/lot label batches with three TSPL templates, and POS unknown-barcode quick-add/attachment are implemented. The 29 September audit fixed metadata loss on edit, hierarchy validation, import unit/barcode guards and list UI gaps; the 2 October follow-up closed the two outstanding software workflows. The local synthetic 20k-product benchmark passed: 4.890 s import and 0.491 ms p95 search. Physical label-printer/scanner calibration, 30 cm scan verification, scaling/keyboard checks, and native Gujarati review remain. |
| 3 — Stock-in | **Software workflows implemented; external sign-off remains** | Migrations 004 and 007-009 cover stock counts, vendor credit notes, saved count scopes and purchase tax-mode snapshots. Posted vendor returns reduce lot stock and vendor balance, record credit variance in P&L, and appear in vendor history. The stock ledger opens source documents, lot views show colored expiry buckets and a direct expired-lot write-off, and draft counts refresh later lots before posting. Purchase, return, count and migration tests pass; rendered Phase 3 screenshots are linked in the audit. Printer/scanner output, scaling and keyboard checks on the target PC, and native Gujarati review remain. |
| 4 — POS | **Software workflows implemented; external sign-off remains** | Product-wide F4 quantity edits reallocate across FEFO lots. Held bills show changed price, MRP and stock before resume; discount entry supports percent and amount; one return can contain several lines. History shows returns and movements and exports to Excel. Day-end has a printable full summary; new receipt reprints use immutable store, tax, width, language and footer snapshots. POS shows printer readiness, last-scanned stock/MRP and held-bill chips. The local Release suite and rendered Phase 4 screenshots pass. Physical printer/drawer, power-pull, store-hardware timing, scaling/keyboard/scanner and native Gujarati sign-offs remain. |
| 5 — Money out | **Complete** | Migration 005 adds replay-safe bulk damage grouping, one regular salary per employee/month, expiry-view hardening and immutability guards for posted damages/depreciation. Searchable damage entry and bulk expiry write-off; active-lot expiry tabs, badge, daily popup, mark-down/block/vendor-list actions; complete expense drafts/categories/repeat-last-month; employees, adjustable salary runs and advances; asset register, exact WDV/SLM depreciation posting and disposal are implemented with period locks, operation idempotency and audit. Damages, salaries, depreciation, expense categories and disposal gains/losses flow through `v_pnl_entry`. The documented depreciation example and integration scenarios pass. Report presentation and the full native Gujarati review remain explicitly owned by Phases 6 and 7. |
| 6 — Insight | **Complete** | Typed P&amp;L statements for arbitrary periods, fiscal-period helpers, monthly and salary matrices, posted/projected asset schedules, independently loaded dashboard metrics, the full operational/accounting report catalog, numeric Excel export and paginated A4 preview/printing are implemented. The dashboard is the shell landing page and includes clickable summary tiles plus margin, trend, payment, expiry, low-stock, valuation, payable, backup and top-product insights. The golden P&amp;L scenario, matrix/schedule, complete query catalog, Excel numeric-format, A4 pagination and 20k-entry fiscal-year performance tests pass; the full Release suite has 57 passing tests. |
| 7 — Hardening & release | **Software scope implemented; external sign-offs remain** | Deterministic five-year data generation, pinned performance/UI tooling, pseudo-localization, weekly integrity/FTS/foreign-key checks, Velopack bootstrap and repeatable packaging, pre-backed-up USB updates, and a release/pilot checklist are implemented. The full-scale performance run, native Gujarati review, physical hardware/power-pull validation and live-store pilot require the target store environment. |

Phase 0 checklist:

- [x] `.slnx`, shared build settings, Central Package Management, analyzers and `.editorconfig`
- [x] All planned source, module, test and tool project boundaries scaffolded
- [x] Fluent WPF shell with `NavigationView` and shared design tokens
- [x] Runtime English/Gujarati resource switching on the demo page
- [x] English digits and fixed `en-IN` sample formatting under Gujarati UI culture
- [x] Serilog daily rolling file configuration with 30-day retention
- [x] Core dependency, resource parity, Gujarati-digit and WPF window smoke tests
- [x] GitHub Actions workflow for restore, format, Release build and tests
- [ ] First successful hosted GitHub Actions run (requires a remote repository)
- [ ] Native Gujarati review of Phase 0 strings

Phase 1 checklist:

- [x] Application paths, required directories and OneDrive-path rejection
- [x] SQLite connection factory, WAL/pragmas and serialized `DbExecutor` writes
- [x] Dapper handlers for fixed-point values and `DateOnly`
- [x] Embedded, contiguous migrations 001–003 with schema and seed verification
- [x] Fixed-point money/quantity/percent, tax, allocation, round-off and fiscal-year tests
- [x] Domain errors, SQLite error mapping, clock boundary and fiscal-year helpers
- [x] Document numbering, period guard, audit log and duplicate-operation guard
- [x] Typed settings snapshot, audited saves, change notifications and concurrency protection
- [x] Argon2id authentication, login throttling, password change and login/idle/manual lock
- [x] Eight-step first-run wizard with validation and persisted setup choices
- [x] Live English/Gujarati setup, login, lock and startup resources with English digits
- [x] Verified backup ZIPs with manifest, SHA-256, integrity checks and retention rotation
- [x] Restore inspection, apply, pending marker, startup validation, audit and rollback-safe undo
- [x] Daily/close backups, operation/temp cleanup, integrity/FTS checks, WAL checkpoint and optimize
- [x] Startup clock, free-space and lot-cache checks with visible warnings and audited override
- [x] Backup/restore round-trip, restore undo, settings concurrency, maintenance and rendered WPF tests
- [x] Release build succeeds with zero warnings and all 18 tests pass
- [x] Property-based numeric tests across the full documented scaled-long range
- [x] Printer test-page actions in the first-run wizard (physical printer sign-off remains)
- [x] Data-root selection, bootstrap update and verified database move from the wizard/settings
- [x] Full restore picker with current-vs-backup comparison and typed `RESTORE` confirmation
- [x] USB export flow and shell backup-status indicator (physical USB sign-off remains)
- [x] Single-instance guard before startup database access
- [ ] Complete public Core/Data XML documentation and native-speaker Gujarati review

Phase 4 checklist:

- [x] Persistent drafts with immediate line, quantity and discount repricing
- [x] Scanner bursts, barcode/SKU search, quantity prefix and price-label quantity
- [x] FEFO allocation with whole-unit, expiry, blocked-lot and stock validation
- [x] Pure fx3 pricing with proportional bill discount, GST and round-off
- [x] Cash, UPI and split payments with tender/change validation
- [x] Idempotent completion, document numbering and stock movements
- [x] Hold/resume with stock validation and refreshed lot prices
- [x] Same-day void reversal with reason, audit and return protection
- [x] Exact pro-rata returns with resellable and customer-return damage paths
- [x] Raster ESC/POS receipts, duplicate reprints, cutter and drawer commands
- [x] Keyset sales history and day-end payment/refund/cash totals
- [x] English/Gujarati POS resources and English-digit receipt formatting
- [x] Golden completion, replay, return, damage, void and day-end tests
- [ ] Physical printer, cutter, drawer and bilingual receipt verification
- [ ] Power-pull and scan-to-line performance verification on store hardware
- [ ] Native Gujarati review of Phase 4 strings

Phase 5 checklist:

- [x] Damage entry uses lot cost snapshots, document numbers and append-only stock movements
- [x] Replay-safe bulk write-off for selected expired lots in one transaction
- [x] Expiry area with expired/critical/soon buckets, mark-down, block and vendor-list printing
- [x] Audited expense categories and quick expense entry
- [x] Repeat-last-month expense drafts preserve the target month's valid day
- [x] Employee master and adjustable salary runs charged to the worked month
- [x] Asset register with WDV/SLM, salvage value and vendor association
- [x] Automatic and manual monthly depreciation posting with locked-period reporting
- [x] Disposal posts partial-month depreciation before gain/loss calculation
- [x] Damages, expenses, salary, depreciation and disposal entries reach P&amp;L
- [x] English/Gujarati resources and English-digit numeric input/display
- [x] Exact worked-example, replay, stock, P&amp;L, payroll and disposal tests
- [x] Complete English/Gujarati resource coverage; native-speaker review is owned by Phase 7

Phase boundary verification: the salary matrix and projected asset schedule are report
presentations owned by Phase 6 (Insight); the full native-speaker Gujarati review is owned by
Phase 7 (Hardening). No Phase 5 implementation item remains open.

Phase 6 checklist:

- [x] Profit and loss statement for custom, month and fiscal-year ranges
- [x] Net sales, COGS, gross profit/margin, expenses, other income and net profit/margin
- [x] Monthly P&amp;L matrix with localized month headings and fiscal-year total column
- [x] Dashboard with today/month sales, payments, 30-day trend, top products and attention metrics
- [x] Sales, item/category/hourly, stock, expiry, damage, expense, salary and asset reports
- [x] Employee-by-month salary matrix and posted/projected per-asset depreciation schedule
- [x] Vendor, purchase, GST accountant summary and audit reports
- [x] Shared report filtering, virtualized grids and totals
- [x] Excel exports use numeric cells, three-decimal formats, real dates and bilingual name columns
- [x] A4 fixed-document preview with repeated headings, page numbers and English digits
- [x] English/Gujarati resource coverage; native-speaker review remains owned by Phase 7
- [x] Golden P&amp;L, matrix, complete query-catalog and Excel-format integration tests
- [x] Fiscal-year P&amp;L stays below 500 ms with 20,000 entries

Phase 7 checklist:

- [x] Deterministic, scalable five-year synthetic data generator with documented full volumes
- [x] Generated databases are verified for SQLite integrity, foreign keys and lot-cache reconciliation
- [x] BenchmarkDotNet, Bogus and FlaUI dependencies pinned after package/license review
- [x] Pseudo-localization mode available through `--pseudo`, with placeholder-preservation tests
- [x] Weekly integrity, foreign-key and FTS checks are scheduled and audited
- [x] Velopack runs before WPF startup; repeatable self-contained release script and pinned CLI added
- [x] Local-folder/USB update flow takes a verified pre-update backup and remembers the folder
- [x] Automated and store go-live checklists are versioned in `docs/release-checklist.md`
- [ ] Full five-year performance run on the target mid-range store PC; results recorded per version
- [x] FlaUI portable-release journey: isolated setup/login, product and vendor creation,
      purchase posting, every main page, scanned cash sale, English/Gujarati switch,
      lock/unlock, manual and close backups
- [ ] Operator walkthrough on the target store PC, including product and purchase entry
- [ ] Native-speaker review of every Gujarati string and printed artifact
- [ ] Scanner, printers, cutter, drawer, power-pull, display-scaling and second-PC restore sign-off
- [ ] One-to-two-week store pilot and owner-approved cut-over

### 21.2 Definition of done (every feature)

- Strings in resources (English), Gujarati translation added (may lag within the phase, tracked).
- Numbers on the new screen/print output verified in English digits with the UI in Gujarati.
- Service-level validation with `ErrorCode`s; DB constraints covering the same rules.
- Unit/integration tests, including one failure-path test.
- Keyboard shortcuts documented in Appendix A; works at 1366×768.
- Audit entries for sensitive changes.
- No new analyzer warnings; architecture tests green.

### 21.3 Backlog (post v1, in rough priority)

1. **Customer credit (udhaar) ledger** — `customer_receipt` table, statements, reminders printout;
   enables the `CREDIT` payment mode.
2. **Cash-register sessions** — opening float, counted cash, differences.
3. **Vendor returns** — return expired/damaged stock to vendor with credit notes.
4. **Purchase orders & reorder suggestions** from sales velocity and lead times.
5. **Historical stock valuation** (as of any date) by ledger replay.
6. **Serial weighing-scale integration** (live weight into the POS via COM port).
7. GS1 2D codes as a standard flow (lot auto-selection by batch/expiry).
8. B2B invoices with buyer GSTIN and IGST for inter-state customers.
9. Additional UI languages, only if the store needs them (§5.12).

---

## Appendix A — Keyboard map

**Global**

| Keys | Action |
|---|---|
| F1 | Shortcut overlay for this page |
| Ctrl+K | Go to… (command palette: pages, products, recent bills) |
| Ctrl+1 … Ctrl+9 | Navigation items 1–9 (1 = Dashboard, 2 = Billing, …) |
| Ctrl+Shift+L | Lock |
| Ctrl+S | Save (forms) |
| Esc | Close dialog / clear search / cancel |
| Enter | Next field (forms) / confirm (dialogs) |

**Billing (POS)**

| Keys | Action |
|---|---|
| F2 | New bill |
| F3 | Focus scan/search box |
| `n*` then scan | Quantity prefix (e.g. `3*`, `1.25*`) |
| F4 | Change quantity of selected line |
| F5 | Line discount |
| F6 | Bill discount |
| Ctrl+L | Change lot of selected line |
| Delete | Remove selected line |
| ↑ / ↓ | Move line selection |
| F7 | Hold bill |
| F8 | Held bills |
| F9 | Pay: cash |
| F10 | Pay: UPI |
| F11 | Pay: split |
| F12 | Complete & print |
| Ctrl+P | Reprint last bill |
| Ctrl+R | Return (find bill) |
| Ctrl+U | Customer |

**Purchase entry**

| Keys | Action |
|---|---|
| F3 | Add product (scan/search) |
| Tab / Enter | Next cell |
| Ctrl+D | Duplicate line (same product, new batch) |
| Ctrl+Enter | Post purchase |

**Dashboard and reports**

| Keys | Action |
|---|---|
| F5 | Refresh the current dashboard/report |
| Ctrl+E | Export the current report to Excel |
| Ctrl+P | Preview and print the current report on A4 |

---

## Appendix B — Error codes

| Code | Raised when | Source | Resource key |
|---|---|---|---|
| `InsufficientStock` | Not enough stock in FEFO lots (negative stock off) | Service; trigger `insufficient stock in lot` | `Error_InsufficientStock` |
| `NoLotForProduct` | Product has never been received | Service | `Error_NoLotForProduct` |
| `ExpiredLot` | Selling from an expired lot | Service | `Error_ExpiredLot` |
| `FractionalQtyNotAllowed` | Decimal qty on a whole-unit product | Service | `Error_FractionalQtyNotAllowed` |
| `SaleFinalized` | Editing/deleting a completed or void bill | Service; triggers | `Error_SaleFinalized` |
| `PurchaseFinalized` | Editing lines of a posted purchase | Service; trigger | `Error_PurchaseFinalized` |
| `LedgerAppendOnly` | Update/delete of a stock movement | Trigger | `Error_LedgerAppendOnly` |
| `LotProductMismatch` | Lot doesn't belong to the product | Service; composite FK | `Error_LotProductMismatch` |
| `PriceAboveMrp` | Selling price > MRP | Service; CHECK | `Error_PriceAboveMrp` |
| `DuplicateVendorBill` | Same vendor + invoice number | Service; UNIQUE | `Error_DuplicateVendorBill` |
| `InvalidDate` | Impossible date (30-02) | Service; CHECK | `Error_InvalidDate` |
| `PeriodLocked` | Date ≤ `books.locked_until` | Service | `Error_PeriodLocked` |
| `BarcodeNotFound` | Scan resolves to nothing | ScanResolver | `Error_BarcodeNotFound` |
| `BarcodeExists` | Barcode already belongs to another product | Service; UNIQUE | `Error_BarcodeExists` |
| `InvalidBarcode` | Bad check digit / length | Service | `Error_InvalidBarcode` |
| `EmptyBill` | Completing a bill without lines | Service | `Error_EmptyBill` |
| `PaymentShort` | Payments < grand total | Service | `Error_PaymentShort` |
| `OverpaymentNonCash` | Change due but no cash payment | Service | `Error_OverpaymentNonCash` |
| `CreditNeedsCustomer` | Credit payment without customer | Service | `Error_CreditNeedsCustomer` |
| `DiscountExceedsLine` | Discount larger than the amount | PricingEngine | `Error_DiscountExceedsLine` |
| `ReturnQtyExceedsSold` | Return qty > sold − returned | Service | `Error_ReturnQtyExceedsSold` |
| `VoidNotAllowed` | Void of old bill or bill with returns | Service | `Error_VoidNotAllowed` |
| `CancelNotAllowed` | Cancel purchase whose lots were used | Service | `Error_CancelNotAllowed` |
| `DatabaseNewerThanApp` | DB `user_version` > app's latest | Migration runner | `Error_DatabaseNewerThanApp` |
| `ClockBehindLastBill` | PC date earlier than last bill date | Startup | `Error_ClockBehindLastBill` |
| `WrongPassword` | Login failed | Auth | `Error_WrongPassword` |
| `BackupInvalid` | Backup set fails manifest, checksum or integrity checks | Backup / restore | `Error_BackupInvalid` |

---

## Appendix C — Settings keys

Seeded by migrations (001/002) or defaulted in code.

| Key | Default | Meaning |
|---|---|---|
| `store.name`, `store.address`, `store.phone`, `store.gstin`, `store.state` | — | Store identity on bills |
| `tax.mode` | `REGULAR` | `REGULAR` / `COMPOSITION` / `NONE` |
| `tax.prices_include_tax` | `1` | Selling prices are GST-inclusive |
| `fy.start_month` | `4` | Financial year start |
| `books.locked_until` | empty | Period lock date |
| `business.day_start_hour` | `0` | Bills before this hour belong to the previous day |
| `bill.round_to` | `1000` | Round-off step (fx3); `0` = off |
| `stock.allow_negative` | `0` | Allow selling beyond recorded stock |
| `expiry.default_alert_days` | `30` | Fallback alert window |
| `expiry.popup_on_login` | `1` | Daily expiry popup |
| `pos.block_expired` | `1` | Exclude expired lots from sale |
| `pos.void_same_day_only` | `1` | Void only today's bills |
| `barcode.lot_prefix` / `barcode.product_prefix` | `20` / `21` | Internal EAN-13 prefixes |
| `scanner.max_gap_ms` / `scanner.min_length` | `35` / `6` | Burst detector thresholds |
| `ui.language` | `en-IN` | UI language: `en-IN` or `gu-IN` |
| `ui.theme` / `ui.backdrop` | `Light` / `None` | Appearance |
| `ui.grid.<name>` | — | Saved column layout |
| `format.always_3dp` | `1` | Show 3 decimals on whole-unit quantities |
| `print.language` | `en-IN` | Language of printed bills/labels: `en-IN` or `gu-IN` |
| `print.bilingual_names` | `1` | Print Gujarati names under English |
| `printer.receipt.name` / `.width_dots` / `.mode` | — / `576` / `RASTER` | Receipt printer |
| `printer.label.name` / `.size` | — / `50x25` | Label printer |
| `printer.a4.name` | — | A4 printer |
| `receipt.footer`, `receipt.footer.gu` | — | Footer text in English / Gujarati |
| `security.idle_lock_minutes` | `15` | Auto-lock |
| `backup.dir` / `backup.keep` | data root / `30` | Backup location and rotation |
| `backup.last_usb_export` | — | For the reminder |

---

## Appendix D — Glossary

| Term | Meaning |
|---|---|
| **MRP** | Maximum Retail Price printed on the pack; legal ceiling for the selling price |
| **GST** | Goods and Services Tax; intra-state = CGST + SGST (half each), inter-state = IGST |
| **HSN** | Harmonized System of Nomenclature code classifying goods for GST |
| **GSTIN** | 15-character GST registration number; first 2 digits = state code (24 = Gujarat) |
| **Composition scheme** | Simplified GST for small businesses; no tax collected on bills, no input credit; issues a *Bill of Supply* |
| **Lot / batch** | Stock received together with the same cost, MRP and expiry (Gujarati UI: બેચ) |
| **FEFO** | First-Expiry-First-Out: sell the lot that expires first |
| **Landed cost** | Unit cost including discounts, freight/other charges and non-creditable tax |
| **COGS** | Cost of goods sold = Σ lot cost of what was sold |
| **GRN** | Goods Receipt Note (here: a posted purchase) |
| **PLU** | Price Look-Up code identifying a loose item on a weighing scale |
| **EAN-13 / UPC-A** | Standard retail barcodes (13 / 12 digits); 890 prefix = GS1 India |
| **GS1 DataMatrix** | 2D code carrying GTIN, batch and expiry |
| **WDV / SLM** | Written-down-value / straight-line depreciation |
| **FY** | Financial year (India: April–March) |
| **Udhaar / khata** | Customer credit (backlog feature) |
| **ESC/POS** | Epson-originated command language of receipt printers |
| **TSPL** | TSC command language of label printers |
| **fx3** | This project's storage format: integer × 1000 |

---

## Appendix E — Coding conventions

The full coding practices, workflow and Definition of Done are in `PLANS.md`; this appendix is the short form.

- File-scoped namespaces; one public type per file; `sealed` by default.
- Names: `PascalCase` types/members, `_camelCase` private fields, `Async` suffix on async methods.
- **No `double`/`float` for business values**; `decimal` literals always with `m`.
- SQL in raw string literals (`"""…"""`), uppercase keywords, explicit column aliases to property names.
- Every SQL parameter bound — never string-interpolate values into SQL (the only exception is
  `PRAGMA user_version = {int}` in the migration runner).
- Repositories: sync, take `SqliteConnection`/`DbTx`. Services: async via `DbExecutor`.
- ViewModels: no `Microsoft.Data.Sqlite`, no `MessageBox` — use `IUserMessages`/`IDialogs`.
- Every user-facing string via resources; no string literals in XAML except symbols/icons.
- Dates in code: `DateOnly` for business dates, `DateTime` (local) for timestamps; never `DateTime.Now`
  directly — use `IClock`.
- Exceptions: `DomainException` for rule violations; let unexpected ones bubble to the global handler.
- Commit messages: `feat(sales): …`, `fix(inventory): …`, `i18n(gu): …`, `perf(search): …`.
