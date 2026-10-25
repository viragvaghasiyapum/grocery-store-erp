using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Localization;
using Serilog;

namespace GroceryErp.App.Presentation;

/// <summary>
/// Shows the upgrade an existing store needs, with its time and space estimate, before anything
/// changes. Update now runs it off the UI thread with its progress; a failure says how to recover.
/// The dialog result is true only when the store is current.
/// </summary>
public partial class UpgradeWindow : Window
{
    private readonly MigrationPlan _plan;
    private readonly Func<IProgress<MigrationProgress>, Task> _upgrade;
    private readonly string _backupDirectory;
    private readonly string _logDirectory;
    private bool _running;

    /// <param name="upgrade">Runs the upgrade, reporting its progress; it must not use the UI thread.</param>
    public UpgradeWindow(MigrationPlan plan, Func<IProgress<MigrationProgress>, Task> upgrade,
        string backupDirectory, string logDirectory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(upgrade);
        InitializeComponent();
        _plan = plan;
        _upgrade = upgrade;
        _backupDirectory = backupDirectory;
        _logDirectory = logDirectory;
        Render();
        Closing += OnClosing;
    }

    private void Render()
    {
        TranslationSource text = TranslationSource.Instance;
        string volume = Path.GetPathRoot(Path.GetFullPath(_backupDirectory)) ?? _backupDirectory;
        ContextText.Text = text.Format("Upgrade_Context", _plan.FromVersion, _plan.ToVersion);
        EstimateText.Text = text.Format("Upgrade_Estimate", Duration(_plan.Estimate));
        string required = CompactionWindow.FormatBytes(_plan.RequiredBytes);
        SpaceText.Text = _plan.FreeBytes < 0
            ? text.Format("Upgrade_SpaceUnknown", required, volume)
            : text.Format("Upgrade_Space", required, volume, CompactionWindow.FormatBytes(_plan.FreeBytes));
        BackupText.Text = text.Format("Upgrade_Backup", _backupDirectory);
        ArchiveText.Text = text["Upgrade_LedgerArchive"];
        ArchiveText.Visibility = _plan.KeepsLedgerArchive ? Visibility.Visible : Visibility.Collapsed;
        // Too little space blocks the upgrade before the store or its backup is touched.
        if (!_plan.HasSpace)
        {
            SpaceWarningText.Text = text.Format("Upgrade_NoSpace",
                CompactionWindow.FormatBytes(_plan.RequiredBytes - _plan.FreeBytes), volume);
            SpaceWarning.Visibility = Visibility.Visible;
            UpgradeButton.IsEnabled = false;
            ExitButton.IsDefault = true;
        }
    }

    private static string Duration(TimeSpan estimate) => estimate.TotalSeconds < 60
        ? TranslationSource.Instance["Upgrade_UnderMinute"]
        : TranslationSource.Instance.Format("Upgrade_Minutes", (int)Math.Ceiling(estimate.TotalMinutes));

    private async void Upgrade_OnClick(object sender, RoutedEventArgs e)
    {
        if (_running || !_plan.HasSpace) return;
        _running = true;
        UpgradeButton.IsEnabled = false;
        ExitButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        // A report queued just before the work ended must not overwrite the result.
        var progress = new Progress<MigrationProgress>(step => { if (_running) ShowProgress(step); });
        try
        {
            await _upgrade(progress);
            _running = false;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Database upgrade from version {From} failed", _plan.FromVersion);
            _running = false;
            ShowFailure(exception);
        }
    }

    internal void ShowProgress(MigrationProgress step)
    {
        ArgumentNullException.ThrowIfNull(step);
        TranslationSource text = TranslationSource.Instance;
        ProgressPanel.Visibility = Visibility.Visible;
        switch (step.Stage)
        {
            case MigrationStage.Backup:
                StageText.Text = text["Upgrade_StageBackup"];
                Progress.IsIndeterminate = true;
                break;
            case MigrationStage.Upgrade:
                StageText.Text = text.Format("Upgrade_StageUpgrade", step.Step, step.Steps);
                Progress.IsIndeterminate = false;
                Progress.Value = step.Steps == 0 ? 0 : (step.Step - 1) * 100d / step.Steps;
                break;
            default:
                StageText.Text = text["Upgrade_StageFinishing"];
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                break;
        }
    }

    internal void ShowFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ProgressPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = UserError.Message(exception);
        RecoveryText.Text = TranslationSource.Instance.Format("Upgrade_Recovery", _backupDirectory, _logDirectory);
        FailurePanel.Visibility = Visibility.Visible;
        UpgradeButton.Visibility = Visibility.Collapsed;
        BackupsButton.Visibility = Visibility.Visible;
        ExitButton.IsEnabled = true;
        ExitButton.IsDefault = true;
        ExitButton.Focus();
    }

    private void Backups_OnClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_backupDirectory}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception exception) { Log.Warning(exception, "The backup folder could not be opened"); }
    }

    private void Exit_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    // An upgrade step must finish once it starts; it is not abandoned by closing the window.
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_running) e.Cancel = true;
    }
}
