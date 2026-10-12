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
            login.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox"))!.AsTextBox().Text = "scale-audit-2026";
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            Window shell = await WaitWindowAsync(app, automation, "BackupStatusButton", TimeSpan.FromMinutes(3));
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
                var record = new { page = label, controlFoundMilliseconds,
                    settledMilliseconds = timer.Elapsed.TotalMilliseconds,
                    workingSet = process?.WorkingSet64, peakWorkingSet = process?.PeakWorkingSet64,
                    alive = !app.HasExited };
                records.Add(record);
                Console.WriteLine(JsonSerializer.Serialize(record));
                if (label is "Products" or "Reports" or "Dashboard")
                    shell.CaptureToFile(Path.Combine(root, "scale-audit-ui-" + label.ToLowerInvariant().Replace(' ', '-') + ".png"));
                Assert.False(app.HasExited, $"App exited on {label}");
            }
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
