using System.Collections.Immutable;
using System.Globalization;
using Dapper;
using CommunityToolkit.Mvvm.Messaging;
using GroceryErp.Core.Errors;
using GroceryErp.Core.Messages;
using GroceryErp.Core.Time;
using GroceryErp.Data.Infrastructure;

namespace GroceryErp.Data.Services;

public sealed record AppSettings(
    string StoreName,
    int FyStartMonth,
    int BusinessDayStartHour,
    DateOnly? BooksLockedUntil,
    int IdleLockMinutes,
    int BackupKeep,
    string BackupDirectory) : IBusinessClockSettings;

public interface ISettings
{
    AppSettings Current { get; }
}

public sealed class SettingsService(DbExecutor database, AppPaths paths, IAuditLog auditLog) : ISettings, IBusinessClockSettings
{
    private ImmutableDictionary<string, string> _values = ImmutableDictionary<string, string>.Empty;
    public AppSettings Current { get; private set; } = Defaults(paths);
    public int BusinessDayStartHour => Current.BusinessDayStartHour;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> loaded = await database.ReadAsync(connection =>
            connection.Query<(string Key, string Value)>("SELECT key AS Key, value AS Value FROM setting")
                .ToDictionary(static row => row.Key, static row => row.Value, StringComparer.Ordinal), cancellationToken);
        _values = loaded.ToImmutableDictionary(StringComparer.Ordinal);
        Current = CreateSnapshot(_values, paths);
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, string> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach ((string key, string value) in changes)
            if (string.IsNullOrWhiteSpace(key) || value is null) throw new DomainException(ErrorCode.Validation, key);

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
        WeakReferenceMessenger.Default.Send(new SettingsChangedMessage(changes.Keys.ToArray()));
    }

    private static AppSettings Defaults(AppPaths paths) => new("", 4, 0, null, 15, 30, paths.DefaultBackupDir);

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

        string locked = Get(values, "books.locked_until", "");
        DateOnly? lockedUntil = DateOnly.TryParseExact(locked, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date : null;
        string backup = Get(values, "backup.dir", "");
        return new(
            Get(values, "store.name", ""),
            GetInt(values, "fy.start_month", 4, 1, 12),
            GetInt(values, "business.day_start_hour", 0, 0, 23),
            lockedUntil,
            GetInt(values, "security.idle_lock_minutes", 15, 1, 1440),
            GetInt(values, "backup.keep", 30, 1, 365),
            string.IsNullOrWhiteSpace(backup) ? paths.DefaultBackupDir : backup);
    }
}

public sealed class PeriodGuard(ISettings settings)
{
    public void EnsureOpen(DateOnly date)
    {
        if (settings.Current.BooksLockedUntil is { } locked && date <= locked)
            throw new DomainException(ErrorCode.PeriodLocked, date, locked);
    }
}
