using System.ComponentModel;
using System.Globalization;
using System.Windows;
using GroceryErp.Data.Backup;
using GroceryErp.Localization;
using Serilog;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Runs a requested compaction after sign-in and before the shell opens, with its progress.
/// Cancel is possible until the compact copy is swapped in.
/// </summary>
// The window disposes its cancellation source when it closes.
#pragma warning disable CA1001
public partial class CompactionWindow : Window
#pragma warning restore CA1001
{
    private readonly BackupService _backups;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _running;

    public CompactionWindow(BackupService backups, bool runOnLoad = true)
    {
        InitializeComponent();
        _backups = backups;
        if (runOnLoad) Loaded += async (_, _) => await RunAsync();
        Closing += OnClosing;
        Closed += (_, _) => _cancellation.Dispose();
    }

    /// <summary>Gets the finished compaction, or null when it was cancelled or failed.</summary>
    public CompactionResult? Result { get; private set; }

    /// <summary>Formats a byte count for people, always with English digits.</summary>
    internal static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? (bytes / (1024d * 1024 * 1024)).ToString("0.00 GB", TranslationSource.FormatCulture)
        : (bytes / (1024d * 1024)).ToString("0 MB", TranslationSource.FormatCulture);

    private async Task RunAsync()
    {
        _running = true;
        // A report queued just before the work ended must not overwrite the result.
        var progress = new Progress<CompactionProgress>(step => { if (_running) ShowProgress(step); });
        TranslationSource text = TranslationSource.Instance;
        try
        {
            Result = await Task.Run(() => _backups.Compact(progress, _cancellation.Token));
            StageText.Text = text["Settings_CompactDone"];
            DetailText.Text = text.Format("Settings_CompactDoneDetail", FormatBytes(Result.BytesBefore),
                FormatBytes(Result.BytesAfter), System.IO.Path.GetFileName(Result.BackupPath));
            Progress.IsIndeterminate = false;
            Progress.Value = 100;
        }
        catch (OperationCanceledException)
        {
            StageText.Text = text["Settings_CompactCancelled"];
            DetailText.Text = string.Empty;
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Database compaction failed");
            StageText.Text = text["Settings_CompactFailed"];
            DetailText.Text = UserError.Message(exception);
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
        }
        finally
        {
            _running = false;
            CancelButton.Visibility = Visibility.Collapsed;
            ContinueButton.Visibility = Visibility.Visible;
            ContinueButton.Focus();
        }
    }

    internal void ShowProgress(CompactionProgress step)
    {
        ArgumentNullException.ThrowIfNull(step);
        TranslationSource text = TranslationSource.Instance;
        StageText.Text = text[$"Settings_CompactStage{step.Stage}"];
        Progress.IsIndeterminate = step.Fraction is null;
        Progress.Value = (step.Fraction ?? 0) * 100;
        DetailText.Text = step.Fraction is { } fraction
            ? text.Format("Settings_CompactPercent", (fraction * 100).ToString("0", CultureInfo.InvariantCulture))
            : string.Empty;
        // The swap is short and must finish once it starts.
        CancelButton.IsEnabled = step.Stage != CompactionStage.Swapping && !_cancellation.IsCancellationRequested;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StageText.Text = TranslationSource.Instance["Settings_CompactCancelling"];
        _cancellation.Cancel();
    }

    private void Continue_OnClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Closing the window while it runs asks to cancel instead of abandoning the work.
        if (!_running) return;
        e.Cancel = true;
        if (CancelButton.IsEnabled) Cancel_OnClick(this, new RoutedEventArgs());
    }
}
