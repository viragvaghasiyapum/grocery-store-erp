using System.IO;
using GroceryErp.Core.Errors;
using GroceryErp.Data.Backup;
using GroceryErp.Data.Services;
using Velopack;

namespace GroceryErp.App;

public sealed record PendingUsbUpdate(UpdateManager Manager, UpdateInfo Update)
{
    public string Version => Update.TargetFullRelease.Version.ToString();
}

public sealed class UsbUpdateService(BackupService backups, SettingsService settings)
{
    public async Task<PendingUsbUpdate?> CheckAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        string fullPath = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullPath)) throw new DomainException(ErrorCode.Validation, fullPath);
        var manager = new UpdateManager(fullPath);
        UpdateInfo? update = await Task.Run(manager.CheckForUpdatesAsync, cancellationToken).ConfigureAwait(false);
        await settings.SaveAsync(new Dictionary<string, string>
        {
            ["update.folder"] = fullPath,
        }, cancellationToken).ConfigureAwait(false);
        return update is null ? null : new(manager, update);
    }

    public async Task DownloadAndRestartAsync(
        PendingUsbUpdate pending,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        await Task.Run(() => backups.Create("pre-update"), cancellationToken).ConfigureAwait(false);
        await pending.Manager.DownloadUpdatesAsync(pending.Update, progress, cancellationToken).ConfigureAwait(false);
        pending.Manager.ApplyUpdatesAndRestart(pending.Update.TargetFullRelease);
    }
}
