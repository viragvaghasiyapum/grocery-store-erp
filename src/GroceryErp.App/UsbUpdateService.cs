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
    public static async Task<PendingUsbUpdate?> CheckAsync(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        string fullPath = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullPath)) throw new DomainException(ErrorCode.Validation, fullPath);
        var manager = new UpdateManager(fullPath);
        UpdateInfo? update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return update is null ? null : new(manager, update);
    }

    public async Task DownloadAndRestartAsync(
        PendingUsbUpdate pending,
        string folderPath,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        backups.Create("pre-update");
        await pending.Manager.DownloadUpdatesAsync(pending.Update, progress, cancellationToken).ConfigureAwait(false);
        await settings.SaveAsync(new Dictionary<string, string>
        {
            ["update.folder"] = Path.GetFullPath(folderPath),
        }, cancellationToken).ConfigureAwait(false);
        pending.Manager.ApplyUpdatesAndRestart(pending.Update.TargetFullRelease);
    }
}
