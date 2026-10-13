using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Xunit;
using UiApplication = FlaUI.Core.Application;

namespace GroceryErp.UI.Tests;

public sealed class ScaleUiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public async Task LargeStore_ModuleNavigationRemainsResponsive()
    {
        string? root = Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_ROOT");
        string? executable = Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXE");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(executable))
        {
            Assert.Skip("Set GROCERY_SCALE_UI_ROOT and GROCERY_SCALE_UI_EXE for an isolated large-store UI run.");
            return;
        }
        string expectedPrefix = Path.Combine(Path.GetTempPath(), "grocery-erp-packaged-ui-");
        if (!Path.GetFullPath(root).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"GROCERY_SCALE_UI_ROOT must be under {expectedPrefix} to match the app's test-store guard.");
        if (!File.Exists(Path.Combine(root, "db", "grocery.db")))
            throw new FileNotFoundException("The isolated UI database is missing.", Path.Combine(root, "db", "grocery.db"));

        var records = new List<object>();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.Environment["GROCERY_ERP_TEST_DATA_ROOT"] = root;
        UiApplication? app = null;
        try
        {
            app = UiApplication.Launch(start);
            using var automation = new UIA3Automation();
            Window login = await WaitWindowAsync(app, automation, "PasswordBox", TimeSpan.FromMinutes(3));
            login.FindFirstDescendant(cf => cf.ByAutomationId("UsernameBox"))!.AsTextBox().Text = "owner";
            login.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox"))!.AsTextBox().Text = "scale-audit-2026";
            login.FindAllDescendants().First(element =>
                element.ControlType == FlaUI.Core.Definitions.ControlType.Button && element.Name == "Sign in").Click();
            Window shell;
            try
            {
                shell = await WaitWindowAsync(app, automation, "BackupStatusButton",
                    Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPORT") == "1"
                        ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(3));
            }
            catch (TimeoutException)
            {
                File.WriteAllText(Path.Combine(root, "scale-audit-login-diagnostics.txt"),
                    string.Join(Environment.NewLine, app.GetAllTopLevelWindows(automation).Select(window =>
                        $"{window.Name}: {string.Join(" | ", window.FindAllDescendants().Take(100).Select(child => child.Name))}")));
                login.CaptureToFile(Path.Combine(root, "scale-audit-login-diagnostics.png"));
                throw;
            }
            foreach (Window other in app.GetAllTopLevelWindows(automation).Where(window =>
                         window.FindFirstDescendant(cf => cf.ByAutomationId("BackupStatusButton")) is null))
                other.FindFirstDescendant(cf => cf.ByName("OK"))?.Click();
            await Task.Delay(2000, TestContext.Current.CancellationToken);
            foreach (Window other in app.GetAllTopLevelWindows(automation).Where(window =>
                         window.FindFirstDescendant(cf => cf.ByAutomationId("BackupStatusButton")) is null))
                other.FindFirstDescendant(cf => cf.ByName("Later"))?.Click();

            foreach ((string label, string control) in new[]
            {
                ("Products", "SearchBox"),
                ("Purchases", "OpenDraftButton"),
                ("Stock", "StockTabs"),
                ("Assets", "AssetName"),
                ("Reports", "ReportPicker"),
                ("Sales history", "HistoryGrid"),
                ("Billing (POS)", "ScanBox"),
                ("Dashboard", "TodaySales"),
                ("Money out", "MoneyOutTabs"),
                ("Assets", "AssetName"),
            })
            {
                var timer = Stopwatch.StartNew();
                File.AppendAllText(Path.Combine(root, "scale-audit-ui-progress.txt"), $"Starting {label}{Environment.NewLine}");
                AutomationElement action = shell.FindFirstDescendant(cf => cf.ByName(label))
                    ?? throw new InvalidOperationException($"Missing navigation action: {label}");
                action.Click();
                try
                {
                    await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId(control)) is not null,
                        TimeSpan.FromSeconds(20));
                }
                catch (TimeoutException)
                {
                    try { shell.CaptureToFile(Path.Combine(root, "scale-audit-ui-timeout.png")); }
                    catch (Exception captureError)
                    {
                        File.AppendAllText(Path.Combine(root, "scale-audit-ui-progress.txt"),
                            $"Capture failed: {captureError.Message}{Environment.NewLine}");
                    }
                    string windows = string.Join(" | ", app.GetAllTopLevelWindows(automation).Select(static window => window.Name));
                    string children = string.Join(" | ", shell.FindAllChildren().Select(static child => child.Name));
                    File.AppendAllText(Path.Combine(root, "scale-audit-ui-progress.txt"),
                        $"Timeout {label}; windows={windows}; shell children={children}{Environment.NewLine}");
                    throw;
                }
                double controlFoundMilliseconds = timer.Elapsed.TotalMilliseconds;
                await Task.Delay(1500, TestContext.Current.CancellationToken);
                using Process? process = Process.GetProcessesByName("GroceryErp.App")
                    .OrderByDescending(static item => item.StartTime).FirstOrDefault();
                process?.Refresh();
                string gridId = label switch
                {
                    "Products" => "ProductsGrid",
                    "Purchases" => "PurchasesGrid",
                    "Stock" => "OverviewGrid",
                    "Assets" => "AssetGrid",
                    "Reports" => "ReportGrid",
                    "Sales history" => "HistoryGrid",
                    "Billing (POS)" => "BillGrid",
                    "Dashboard" => "RevenueGrid",
                    "Money out" => "MoneyOutTabs",
                    _ => throw new InvalidOperationException($"Unknown page: {label}"),
                };
                int gridRowItems = shell.FindFirstDescendant(cf => cf.ByAutomationId(gridId))?
                    .FindAllDescendants().Count(element =>
                        element.ControlType == FlaUI.Core.Definitions.ControlType.DataItem) ?? 0;
                var record = new { page = label, controlFoundMilliseconds,
                    settledMilliseconds = timer.Elapsed.TotalMilliseconds,
                    workingSet = process?.WorkingSet64, peakWorkingSet = process?.PeakWorkingSet64,
                    gridRowItems, alive = !app.HasExited };
                records.Add(record);
                Console.WriteLine(JsonSerializer.Serialize(record));
                if (label is "Products" or "Reports" or "Dashboard")
                    shell.CaptureToFile(Path.Combine(root, "scale-audit-ui-" + label.ToLowerInvariant().Replace(' ', '-') + ".png"));
                Assert.False(app.HasExited, $"App exited on {label}");
                if (label == "Billing (POS)" &&
                    Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_SAVED_BILLS") == "1")
                {
                    // Populated fixtures can open the saved-bill picker on entry.
                    // Dismiss it before navigating to another shell page.
                    Window savedBills = await WaitWindowAsync(app, automation, "BillsGrid", TimeSpan.FromSeconds(15));
                    (savedBills.FindFirstDescendant(cf => cf.ByName("Cancel"))
                        ?? throw new InvalidOperationException("Saved-bill picker has no Cancel action.")).Click();
                    await WaitUntilAsync(() => app.GetAllTopLevelWindows(automation).All(window =>
                        window.FindFirstDescendant(cf => cf.ByAutomationId("BillsGrid")) is null),
                        TimeSpan.FromSeconds(10));
                }
            }
            if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_DIALOGS") == "1")
                await AuditDialogsAsync(app, automation, shell, root);
            if (Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPORT") == "1")
                await ExportFullHistoryAsync(app, automation, shell, root);
            shell.Close();
            await WaitUntilAsync(() => app.HasExited, TimeSpan.FromMinutes(3));
        }
        finally
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "scale-audit-ui.json"),
                JsonSerializer.Serialize(records, JsonOptions));
            if (app is { HasExited: false }) app.Kill();
            app?.Dispose();
        }
    }

    private static async Task AuditDialogsAsync(UiApplication app, UIA3Automation automation,
        Window shell, string root)
    {
        var records = new List<object>();
        foreach ((string page, string action, string control) in new[]
        {
            ("Stock", "ResumeCountButton", "DraftGrid"),
            ("Purchases", "PaymentButton", "PurchaseSearchBox"),
        })
        {
            shell.FindFirstDescendant(cf => cf.ByName(page))!.Click();
            await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId(action)) is not null,
                TimeSpan.FromSeconds(20));
            var timer = Stopwatch.StartNew();
            shell.FindFirstDescendant(cf => cf.ByAutomationId(action))!.Click();
            Window dialog = await WaitWindowAsync(app, automation, control, TimeSpan.FromSeconds(20));
            AutomationElement list = dialog.FindFirstDescendant(cf =>
                cf.ByAutomationId(control == "DraftGrid" ? "DraftGrid" : "PurchaseBox"))!;
            int visibleDataItems = list.FindAllDescendants().Count(element =>
                element.ControlType == FlaUI.Core.Definitions.ControlType.DataItem);
            records.Add(new { page, control, readyMilliseconds = timer.Elapsed.TotalMilliseconds,
                visibleDataItems });
            File.WriteAllText(Path.Combine(root, "scale-audit-dialogs.json"),
                JsonSerializer.Serialize(records, JsonOptions));
            AutomationElement cancel = dialog.FindFirstDescendant(cf => cf.ByName("Cancel"))
                ?? throw new InvalidOperationException($"The {control} dialog has no Cancel action.");
            cancel.Click();
            await WaitUntilAsync(() => app.GetAllTopLevelWindows(automation).All(window =>
                window.FindFirstDescendant(cf => cf.ByAutomationId(control)) is null), TimeSpan.FromSeconds(20));
        }
        File.WriteAllText(Path.Combine(root, "scale-audit-dialogs.json"),
            JsonSerializer.Serialize(records, JsonOptions));
    }

    private static async Task ExportFullHistoryAsync(UiApplication app, UIA3Automation automation,
        Window shell, string root)
    {
        shell.FindFirstDescendant(cf => cf.ByName("Sales history"))!.Click();
        await WaitUntilAsync(() => shell.FindFirstDescendant(cf => cf.ByAutomationId("ExportButton")) is not null,
            TimeSpan.FromSeconds(20));
        string? dateText = Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPORT_DATE");
        foreach ((string picker, string? selected) in new[]
        {
            ("FromPicker", Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPORT_FROM_DATE") ?? dateText),
            ("ToPicker", Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPORT_TO_DATE") ?? dateText),
        })
        {
            if (selected is null) continue;
            DateTime date = DateTime.ParseExact(selected, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture);
            AutomationElement input = shell.FindFirstDescendant(cf => cf.ByAutomationId(picker))!
                .FindFirstDescendant(cf => cf.ByAutomationId("PART_TextBox"))!;
            input.AsTextBox().Text = date.ToShortDateString();
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
        }
        string path = Path.Combine(root, "phase1-five-million-history-" + Guid.NewGuid().ToString("N") + ".csv");
        var timer = Stopwatch.StartNew();
        shell.FindFirstDescendant(cf => cf.ByAutomationId("ExportButton"))!.Click();
        await Task.Delay(700, TestContext.Current.CancellationToken);
        Window? save = null;
        try
        {
            await WaitUntilAsync(() =>
            {
                save = automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Save As"))?.AsWindow();
                return save is not null;
            }, TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            File.WriteAllText(Path.Combine(root, "scale-audit-export-dialogs.txt"),
                string.Join(Environment.NewLine, automation.GetDesktop().FindAllChildren()
                    .Where(element => element.ControlType == FlaUI.Core.Definitions.ControlType.Window)
                    .Take(20).Select(window => $"{SafeName(window)}: {string.Join(" | ", window.FindAllDescendants().Take(100).Select(SafeName))}")));
            shell.CaptureToFile(Path.Combine(root, "scale-audit-export-dialogs.png"));
            throw;
        }
        Keyboard.Type(path);
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        await WaitUntilAsync(() => File.Exists(path), TimeSpan.FromMinutes(15));
        timer.Stop();
        long count = File.ReadLines(path).LongCount() - 1;
        long expected = long.Parse(Environment.GetEnvironmentVariable("GROCERY_SCALE_UI_EXPECT_EXPORT_ROWS") ?? "5000000",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, count);
        if (expected == 5_000_000)
        {
            string firstRow = File.ReadLines(path).Skip(1).First();
            Assert.Contains("\"PHASE1-5000000\"", firstRow, StringComparison.Ordinal);
            Assert.EndsWith(",1.00", firstRow, StringComparison.Ordinal);
        }
        Assert.False(app.HasExited);
        using Process? process = Process.GetProcessesByName("GroceryErp.App")
            .OrderByDescending(static item => item.StartTime).FirstOrDefault();
        process?.Refresh();
        File.WriteAllText(Path.Combine(root, "scale-audit-history-export.json"),
            JsonSerializer.Serialize(new { rows = count, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                bytes = new FileInfo(path).Length, peakWorkingSet = process?.PeakWorkingSet64,
                alive = !app.HasExited }, JsonOptions));
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name; }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException) { return "<unavailable>"; }
    }

    private static async Task<Window> WaitWindowAsync(UiApplication app, UIA3Automation automation,
        string automationId, TimeSpan timeout)
    {
        Window? found = null;
        await WaitUntilAsync(() =>
        {
            found = app.GetAllTopLevelWindows(automation).FirstOrDefault(window =>
                window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not null);
            return found is not null;
        }, timeout);
        return found!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > timeout) throw new TimeoutException($"UI did not reach expected state within {timeout}.");
            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }
    }
}
