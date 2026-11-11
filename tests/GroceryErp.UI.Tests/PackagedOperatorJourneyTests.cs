using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Dapper;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using GroceryErp.Catalog.Barcodes;
using GroceryErp.Data.Infrastructure;
using Shouldly;
using Xunit;
using PackagedApplication = FlaUI.Core.Application;

namespace GroceryErp.UI.Tests;

public sealed class PackagedOperatorJourneyTests
{
    [Fact]
    public async Task PortableRelease_OperatorJourneyAndBackup_WorkOffline()
    {
        if (Environment.GetEnvironmentVariable("GROCERY_PACKAGED_UI_TEST") != "1")
        {
            Assert.Skip("Set GROCERY_PACKAGED_UI_TEST=1 after building the portable release.");
            return;
        }

        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string archivePath = Path.Combine(repository, "artifacts", "Releases", "GroceryErp-win-Portable.zip");
        File.Exists(archivePath).ShouldBeTrue($"Build {archivePath} first.");
        string root = Path.Combine(Path.GetTempPath(), $"grocery-erp-packaged-ui-{Guid.NewGuid():N}");
        string package = Path.Combine(root, "package");
        string store = Path.Combine(root, "store");
        Directory.CreateDirectory(package);
        ZipFile.ExtractToDirectory(archivePath, package);
        string executable = Path.Combine(package, "current", "GroceryErp.App.exe");
        File.Exists(executable).ShouldBeTrue();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.Environment["GROCERY_ERP_TEST_DATA_ROOT"] = store;
        PackagedApplication? app = null;
        try
        {
            app = PackagedApplication.Launch(start);
            using var automation = new UIA3Automation();
            Window setup = await WaitWindowAsync(app, automation, "NextButton");
            // A mouse click can land before the new window takes input; invoking the button cannot.
            Invoke(setup, "NextButton");
            Enter(setup, "StoreName", "Packaged UI Test Store");
            Invoke(setup, "NextButton");
            Enter(setup, "Password", "test-owner-2026");
            Enter(setup, "ConfirmPassword", "test-owner-2026");
            for (int step = 2; step < 8; step++) Invoke(setup, "NextButton");

            Window login = await WaitWindowAsync(app, automation, "PasswordBox");
            SeedCategory(store);
            string barcode = Ean.Internal("21", 1);
            Enter(login, "PasswordBox", "test-owner-2026");
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            Window shell = await WaitWindowAsync(app, automation, "BackupStatusButton");
            shell.FindFirstDescendant(cf => cf.ByAutomationId("UpdateButton")).ShouldNotBeNull();
            shell.FindFirstDescendant(cf => cf.ByAutomationId("LockButton")).ShouldNotBeNull();

            // The first sign-in after installing shows the release notes, owned by the shell, so UI
            // Automation lists them under it. They name the release and must be closed first.
            Window? notes = null;
            await WaitUntilAsync(() => (notes = shell.ModalWindows.FirstOrDefault(window =>
                window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")) is not null)) is not null,
                TimeSpan.FromSeconds(15));
            Find(notes!, "VersionText").Name.ShouldStartWith("Hare Krushna ");
            Screen(notes!, "1-release-notes");
            Invoke(notes!, "CloseButton");
            await WaitUntilAsync(() => shell.ModalWindows.Length == 0, TimeSpan.FromSeconds(10));

            ClickNamed(shell, "Products");
            await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("SearchBox")) is not null,
                TimeSpan.FromSeconds(10));
            ClickNamed(shell, "New product");
            Window product = await WaitWindowAsync(app, automation, "BarcodesBox");
            Enter(product, "NameBox", "Packaged Test Rice");
            Find(product, "HsnBox").AsTextBox().Text = "1006";
            // The price fields are below the fold of the form, where a click-and-type misses them.
            Find(product, "PriceBox").AsTextBox().Text = "30";
            Find(product, "MrpBox").AsTextBox().Text = "35";
            Find(product, "PriceBox").AsTextBox().Text.ShouldBe("30");
            Find(product, "BarcodesBox").AsTextBox().Text = barcode;
            Find(product, "BarcodesBox").AsTextBox().Text.ShouldBe(barcode);
            await WaitUntilAsync(() => Find(product, "SaveButton").IsEnabled, TimeSpan.FromSeconds(10));
            Click(product, "SaveButton");
            try
            {
                await WaitUntilAsync(() => ProductExists(store, barcode) ||
                    !string.IsNullOrWhiteSpace(Find(product, "ErrorText").Name), TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Product editor did not save: name={Find(product, "NameBox").AsTextBox().Text}, " +
                    $"barcode={Find(product, "BarcodesBox").AsTextBox().Text}, " +
                    $"saveEnabled={Find(product, "SaveButton").IsEnabled}, error={Find(product, "ErrorText").Name}");
            }
            if (!ProductExists(store, barcode))
                throw new InvalidOperationException($"Product editor rejected the entry: {Find(product, "ErrorText").Name}");

            await NavigateAsync(shell, "Vendors", "VendorsGrid");
            ClickNamed(shell, "New vendor");
            Window vendor = await WaitWindowAsync(app, automation, "ContactBox");
            Enter(vendor, "NameBox", "Packaged Test Supplier");
            Click(vendor, "SaveButton");
            await WaitUntilAsync(() => VendorExists(store), TimeSpan.FromSeconds(10));
            await NavigateAsync(shell, "Purchases", "PurchasesGrid");
            ClickNamed(shell, "New purchase");
            Window purchase = await WaitWindowAsync(app, automation, "ProductSearchBox");
            purchase.FindFirstDescendant(cf => cf.ByAutomationId("VendorBox"))!.AsComboBox()
                .Select("Packaged Test Supplier");
            Enter(purchase, "InvoiceBox", "PKG-INV-001");
            Enter(purchase, "ProductSearchBox", barcode);
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            try { await WaitUntilAsync(() => PurchaseRows(purchase).Length >= 1, TimeSpan.FromSeconds(10)); }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Purchase scan did not add a line: rows={PurchaseRows(purchase).Length}, " +
                    $"search={Find(purchase, "ProductSearchBox").AsTextBox().Text}, " +
                    $"error={Find(purchase, "ErrorText").Name}");
            }
            EditPurchaseCell(purchase, "Batch", "PKG-BATCH");
            EditPurchaseCell(purchase, "Expiry", DateTime.Today.AddYears(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            EditPurchaseCell(purchase, "Rate", "20");
            Screen(purchase, "2-purchase");
            Click(purchase, "PostButton");
            await WaitUntilAsync(() => PurchaseExists(store), TimeSpan.FromSeconds(10));
            foreach (Window prompt in app.GetAllTopLevelWindows(automation).Where(window =>
                         window.FindFirstDescendant(cf => cf.ByName("No")) is not null))
                prompt.FindFirstDescendant(cf => cf.ByName("No"))?.Click();

            // The Stock page and its ledger screens are gone; their work lives in Products.
            shell.FindFirstDescendant(cf => cf.ByName("Stock")).ShouldBeNull();
            await NavigateAsync(shell, "Money out", "MoneyOutTabs");
            await NavigateAsync(shell, "Assets", "AssetGrid");
            await NavigateAsync(shell, "Reports", "ReportPicker");
            await NavigateAsync(shell, "Sales history", "HistoryGrid");
            ClickNamed(shell, "Billing (POS)");
            await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("ScanBox")) is not null,
                TimeSpan.FromSeconds(10));
            Enter(shell, "ScanBox", barcode);
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            await WaitUntilAsync(() => CountSalesRows(store, "sale_line") == 1, TimeSpan.FromSeconds(10));
            Screen(shell, "3-billing");
            ClickNamed(shell, "F9 Cash");
            Window payment = await WaitWindowAsync(app, automation, "CashBox");
            Screen(payment, "4-payment");
            ClickNamed(payment, "Complete bill");
            try { await WaitUntilAsync(() => CountSalesRows(store, "sale") == 1, TimeSpan.FromSeconds(10)); }
            catch (TimeoutException)
            {
                throw new TimeoutException("The bill did not complete. Sales: " + SaleStates(store) + " Windows: " + string.Join(" || ",
                    app.GetAllTopLevelWindows(automation).Select(window => window.Name + ": " + string.Join(" | ",
                        window.FindAllDescendants().Select(element => element.Name)
                            .Where(static name => !string.IsNullOrWhiteSpace(name)).Skip(55).Take(120)))));
            }
            ClickNamed(shell, "Dashboard");
            await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("TodaySales")) is not null,
                TimeSpan.FromSeconds(10));

            Screen(shell, "5-dashboard");
            shell.FindFirstDescendant(cf => cf.ByName("ગુ"))?.Click();
            Find(shell, "LockButton").Name.ShouldBe("લૉક");
            Screen(shell, "6-dashboard-gujarati");
            Click(shell, "LockButton");
            Enter(shell, "UnlockPassword", "test-owner-2026");
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("UnlockPassword")) is null,
                TimeSpan.FromSeconds(10));

            Click(shell, "BackupStatusButton");
            await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("BackupButton")) is not null,
                TimeSpan.FromSeconds(10));
            Click(shell, "BackupButton");
            await WaitUntilAsync(() => Directory.Exists(Path.Combine(store, "backups")) &&
                Directory.EnumerateFiles(Path.Combine(store, "backups"), "*_manual.zip").Any(), TimeSpan.FromSeconds(30));
            Screen(shell, "7-backup");
            shell.Close();
            await WaitUntilAsync(() => app.HasExited, TimeSpan.FromSeconds(30));
            Directory.EnumerateFiles(Path.Combine(store, "backups"), "*_close.zip").ShouldNotBeEmpty();
        }
        finally
        {
            if (app is { HasExited: false }) app.Kill();
            app?.Dispose();
            try { await WaitUntilAsync(() => TryDeleteDirectory(root), TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { Trace.TraceWarning("Test package could not be removed: {0}", root); }
        }
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    // GROCERY_PACKAGED_UI_SCREENS names a folder for a picture of each step; the run's store is deleted.
    private static void Screen(AutomationElement window, string name)
    {
        string? folder = Environment.GetEnvironmentVariable("GROCERY_PACKAGED_UI_SCREENS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        // On a scaled display a process that is not DPI-aware captures only part of the window.
        SetProcessDpiAwarenessContext(-4);
        Thread.Sleep(500);
        window.CaptureToFile(Path.Combine(folder, name + ".png"));
    }

    private static async Task<Window> WaitWindowAsync(PackagedApplication app, UIA3Automation automation, string automationId)
    {
        Window? found = null;
        await WaitUntilAsync(() =>
        {
            found = app.GetAllTopLevelWindows(automation)
                .FirstOrDefault(window => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null);
            return found is not null;
        }, TimeSpan.FromSeconds(45));
        return found!;
    }

    // A control on a page that is still being shown can take a moment to appear.
    private static AutomationElement Find(Window window, string automationId)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is { } found) return found;
            if (watch.Elapsed >= TimeSpan.FromSeconds(5))
                throw new InvalidOperationException($"The {automationId} control is missing.");
            Thread.Sleep(100);
        }
    }

    private static void Click(Window window, string automationId) => Find(window, automationId).Click();

    private static void Invoke(Window window, string automationId) => Find(window, automationId).AsButton().Invoke();

    private static void ClickNamed(Window window, string name) =>
        (window.FindFirstDescendant(cf => cf.ByName(name))
         ?? throw new InvalidOperationException($"The {name} action is missing.")).Click();

    private static async Task NavigateAsync(Window shell, string name, string automationId)
    {
        ClickNamed(shell, name);
        await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null,
            TimeSpan.FromSeconds(10));
    }

    private static void SeedCategory(string root)
    {
        using var connection = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(root))).Open();
        connection.Execute("INSERT INTO category(name) VALUES ('Staples')");
    }

    private static bool ProductExists(string root, string barcode)
    {
        using var connection = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(root))).Open();
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_barcode WHERE barcode = @barcode", new { barcode }) == 1;
    }

    private static bool VendorExists(string root)
    {
        using var connection = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(root))).Open();
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM vendor WHERE name = 'Packaged Test Supplier'") == 1;
    }

    private static AutomationElement[] PurchaseRows(Window purchase) =>
        Find(purchase, "LinesGrid").FindAllDescendants()
            .Where(element => element.ControlType.ToString() == "DataItem").ToArray();

    // Finds the scanned line's cell by its column header, so added or hidden columns do not move it.
    private static void EditPurchaseCell(Window purchase, string header, string value)
    {
        AutomationElement row = PurchaseRows(purchase)[0];
        AutomationElement[] cells = row.FindAllChildren();
        AutomationElement cell = cells.FirstOrDefault(element => element.Patterns.TableItem.PatternOrDefault?
                .ColumnHeaderItems.ValueOrDefault?.FirstOrDefault()?.Name == header)
            ?? throw new InvalidOperationException($"Purchase column {header} was not found ({cells.Length} cells).");
        cell.DoubleClick();
        Keyboard.Type(value);
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
    }

    private static bool PurchaseExists(string root)
    {
        using var connection = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(root))).Open();
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM purchase WHERE status = 'POSTED'") == 1;
    }

    private static long CountSalesRows(string root, string table)
    {
        using var connection = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(root))).Open();
        return table switch
        {
            "sale_line" => connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale_line"),
            "sale" => connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sale WHERE status = 'COMPLETED'"),
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
    }

    private static string SaleStates(string root)
    {
        using var connection = new SqliteConnectionFactory(new AppPaths(new SettingsBootstrap(root))).Open();
        return string.Join(", ", connection.Query<(string Status, long Count, long Total)>(
            "SELECT status AS Status,COUNT(*) AS Count,SUM(grand_total) AS Total FROM sale GROUP BY status")
            .Select(static row => $"{row.Status}={row.Count}/{row.Total}"));
    }

    private static void Enter(Window window, string automationId, string value)
    {
        AutomationElement field = Find(window, automationId);
        field.Click();
        Keyboard.Type(value);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed >= timeout) throw new TimeoutException("The packaged UI did not reach the expected state.");
            await Task.Delay(100);
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return true;
        try { Directory.Delete(path, recursive: true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
