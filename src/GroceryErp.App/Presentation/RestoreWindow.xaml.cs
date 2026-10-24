using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Localization;
using Wpf.Ui.Controls;

namespace GroceryErp.App.Presentation;

public partial class RestoreWindow : FluentWindow
{
    private readonly CurrentDatabaseInfo _current;
    private readonly BackupManifest _backup;

    public RestoreWindow(CurrentDatabaseInfo current, BackupManifest backup)
    {
        InitializeComponent();
        _current = current;
        _backup = backup;
        UpdateSummaries();
        TranslationSource.Instance.PropertyChanged += TranslationChanged;
        Closed += (_, _) => TranslationSource.Instance.PropertyChanged -= TranslationChanged;
    }

    private void TranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]") UpdateSummaries();
    }

    private void UpdateSummaries()
    {
        CurrentSummary.Text = FormatSummary(_current.SchemaVersion, _current.Summary, null, null, null);
        BackupSummary.Text = FormatSummary(_backup.SchemaVersion, _backup.Summary,
            _backup.CreatedAt, _backup.AppVersion, _backup.Reason);
    }

    private static string FormatSummary(long version, BackupSummary summary, string? createdAt,
        string? appVersion, string? reason)
    {
        TranslationSource source = TranslationSource.Instance;
        string date = createdAt is null ? "" : source.Format("Settings_CreatedAt",
            DateTime.TryParseExact(createdAt, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime parsed)
                ? parsed.ToString("dd MMM yyyy HH:mm", TranslationSource.FormatCulture) : createdAt) + "\n";
        string app = appVersion is null ? "" : source.Format("Settings_AppVersion", appVersion) + "\n";
        string why = reason is null ? "" : source.Format("Settings_Reason", LocalizeReason(source, reason)) + "\n";
        return date + app + why + source.Format("Settings_SchemaVersion", version.ToString(CultureInfo.InvariantCulture)) + "\n" +
            source.Format("Settings_ProductCount", summary.Products.ToString("N0", TranslationSource.FormatCulture)) + "\n" +
            source.Format("Settings_BillCount", summary.Bills.ToString("N0", TranslationSource.FormatCulture)) + "\n" +
            source.Format("Settings_LastInvoice", summary.LastInvoiceNo ?? "—") + "\n" +
            source.Format("Settings_LastSale", summary.LastSaleDate ?? "—");
    }

    private static string LocalizeReason(TranslationSource source, string reason) => reason switch
    {
        "manual" => source["Settings_BackupReason_Manual"],
        "daily" => source["Settings_BackupReason_Daily"],
        "close" => source["Settings_BackupReason_Close"],
        "pre-restore" => source["Settings_BackupReason_PreRestore"],
        "pre-update" => source["Settings_BackupReason_PreUpdate"],
        "pre-data-move" => source["Settings_BackupReason_PreDataMove"],
        _ when reason.StartsWith("pre-migration-v", StringComparison.Ordinal) =>
            source.Format("Settings_BackupReason_PreMigration", reason[15..]),
        _ when reason.StartsWith(MigrationRunner.LedgerArchiveReason + "-v", StringComparison.Ordinal) =>
            source.Format("Settings_BackupReason_LedgerArchive", reason[(MigrationRunner.LedgerArchiveReason.Length + 2)..]),
        "pre-compact" => source["Settings_BackupReason_PreCompact"],
        _ => reason,
    };

    private void Confirmation_OnTextChanged(object sender, TextChangedEventArgs e) =>
        RestoreButton.IsEnabled = string.Equals(Confirmation.Text, "RESTORE", StringComparison.Ordinal);

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Restore_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
