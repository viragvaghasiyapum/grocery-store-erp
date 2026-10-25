using System.Collections.Immutable;
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Messages;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Data.Services;

public sealed record AppSettings(
    string StoreName,
    int FyStartMonth,
    int BusinessDayStartHour,
    int IdleLockMinutes,
    int BackupKeep,
    string BackupDirectory) : IBusinessClockSettings
{
    public IReadOnlyDictionary<string, string> Values { get; init; } = ImmutableDictionary<string, string>.Empty;
    public string StoreAddress => Get("store.address", "");
    public string StorePhone => Get("store.phone", "");
    public string StoreGstin => Get("store.gstin", "");
    public string StoreState => Get("store.state", "");
    public string TaxMode => Get("tax.mode", "REGULAR");
    public bool PricesIncludeTax => GetBool("tax.prices_include_tax", true);
    public int BillRoundTo => GetInt("bill.round_to", 1000, 0, int.MaxValue);
    public int ExpiryDefaultAlertDays => GetInt("expiry.default_alert_days", 30, 0, 3650);
    public bool BlockExpiredLots => GetBool("pos.block_expired", true);
    public bool VoidSameDayOnly => GetBool("pos.void_same_day_only", true);
    public string LotPrefix => Get("barcode.lot_prefix", "20");
    public string ProductPrefix => Get("barcode.product_prefix", "21");
    public int ScannerMaxGapMs => GetInt("scanner.max_gap_ms", 35, 1, 1000);
    public int ScannerMinLength => GetInt("scanner.min_length", 6, 1, 100);
    public string ScannerComPort => Get("scanner.com_port", "");
    public string UiLanguage => Get("ui.language", "en-IN");
    public string UiTheme => Get("ui.theme", "Light");
    public string UiBackdrop => Get("ui.backdrop", "None");
    // Software drawing keeps the graphics driver's memory out of the process; the owner can opt into the GPU.
    public bool HardwareRendering => GetBool("ui.hardware_rendering", false);
    public bool AlwaysShowThreeDecimals => GetBool("format.always_3dp", true);
    public string PrintLanguage => Get("print.language", "en-IN");
    public bool PrintBilingualNames => GetBool("print.bilingual_names", true);
    public string ReceiptPrinterName => Get("printer.receipt.name", "");
    public int ReceiptPrinterWidthDots => GetInt("printer.receipt.width_dots", 576, 1, 2400);
    public string ReceiptPrinterMode => Get("printer.receipt.mode", "RASTER");
    public string LabelPrinterName => Get("printer.label.name", "");
    public string LabelSize => Get("printer.label.size", "50x25");
    public string A4PrinterName => Get("printer.a4.name", "");
    public string ReceiptFooter => Get("receipt.footer", "");
    public string GujaratiReceiptFooter => Get("receipt.footer.gu", "");
    public string LastUsbExport => Get("backup.last_usb_export", "");
    public int UsbReminderDays => GetInt("backup.usb_reminder_days", 7, 1, 365);
    public string ScaleRule => Get("barcode.scale_rule", "");
    public bool GujaratiKeyboardAvailable => GetBool("setup.keyboard.gujarati_available", false);
    public string StartChoice => Get("setup.start_choice", "EMPTY");

    private string Get(string key, string fallback) => Values.TryGetValue(key, out string? value) ? value : fallback;
    private int GetInt(string key, int fallback, int minimum, int maximum)
    {
        return int.TryParse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int value) && value >= minimum && value <= maximum ? value : fallback;
    }
    private bool GetBool(string key, bool fallback) => Get(key, fallback ? "1" : "0") is "1" or "true";
}

public interface ISettings
{
    AppSettings Current { get; }
}

public sealed class SettingsService(DbExecutor database, AppPaths paths, IAuditLog auditLog) : ISettings, IBusinessClockSettings, IDisposable
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private ImmutableDictionary<string, string> _values = ImmutableDictionary<string, string>.Empty;
    public AppSettings Current { get; private set; } = Defaults(paths);
    public int BusinessDayStartHour => Current.BusinessDayStartHour;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, string> loaded = await database.ReadAsync(connection =>
                connection.Query<(string Key, string Value)>("SELECT key AS Key, value AS Value FROM setting")
                    .ToDictionary(static row => row.Key, static row => row.Value, StringComparer.Ordinal), cancellationToken);
            _values = loaded.ToImmutableDictionary(StringComparer.Ordinal);
            Current = CreateSnapshot(_values, paths);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, string> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach ((string key, string value) in changes)
            if (string.IsNullOrWhiteSpace(key) || value is null) throw new DomainException(ErrorCode.Validation, key);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            await database.WriteAsync(transaction =>
            {
                foreach ((string key, string value) in changes)
                {
                    string? oldValue = _values.TryGetValue(key, out string? previous) ? previous : null;
                    transaction.Connection.Execute("""
                        INSERT INTO setting(key, value) VALUES (@key, @value)
                        ON CONFLICT(key) DO UPDATE SET value = excluded.value
                        """, new { key, value }, transaction.Transaction);
                    auditLog.Write(transaction, "setting", 0, "UPDATE", new { old = oldValue, @new = value });
                }
            }, cancellationToken);

            _values = _values.SetItems(changes);
            Current = CreateSnapshot(_values, paths);
        }
        finally
        {
            _mutationGate.Release();
        }
        WeakReferenceMessenger.Default.Send(new SettingsChangedMessage(changes.Keys.ToArray()));
    }

    public void Dispose() => _mutationGate.Dispose();

    private static AppSettings Defaults(AppPaths paths) => new("", 4, 0, 15, 30, paths.DefaultBackupDir);

    private static AppSettings CreateSnapshot(IReadOnlyDictionary<string, string> values, AppPaths paths)
    {
        static string Get(IReadOnlyDictionary<string, string> source, string key, string fallback) =>
            source.TryGetValue(key, out string? value) ? value : fallback;
        static int GetInt(IReadOnlyDictionary<string, string> source, string key, int fallback, int minimum, int maximum)
        {
            string raw = Get(source, key, fallback.ToString(CultureInfo.InvariantCulture));
            return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed >= minimum && parsed <= maximum
                ? parsed : fallback;
        }

        string backup = Get(values, "backup.dir", "");
        return new AppSettings(
            Get(values, "store.name", ""),
            GetInt(values, "fy.start_month", 4, 1, 12),
            GetInt(values, "business.day_start_hour", 0, 0, 23),
            GetInt(values, "security.idle_lock_minutes", 15, 1, 1440),
            GetInt(values, "backup.keep", 30, 1, 365),
            string.IsNullOrWhiteSpace(backup) ? paths.DefaultBackupDir : backup)
        {
            Values = values.ToImmutableDictionary(StringComparer.Ordinal),
        };
    }
}
