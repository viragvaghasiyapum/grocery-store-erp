using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using GroceryErp.Core.Errors;
using Microsoft.Data.Sqlite;

namespace GroceryErp.Data.Infrastructure;

public interface IMigrationBackup
{
    void CreateBeforeMigration(string reason);
}

public sealed class NullMigrationBackup : IMigrationBackup
{
    public static NullMigrationBackup Instance { get; } = new();
    private NullMigrationBackup() { }
    public void CreateBeforeMigration(string reason) { }
}

public sealed partial class MigrationRunner(SqliteConnectionFactory factory, IMigrationBackup backup)
{
    public const int LatestVersion = 11;

    public void Run()
    {
        DapperSetup.Register();
        factory.EnsureWal();
        List<Script> scripts = LoadEmbedded();
        using SqliteConnection connection = factory.Open();
        long current = connection.ExecuteScalar<long>("PRAGMA user_version;");
        if (current > LatestVersion)
            throw new DomainException(ErrorCode.DatabaseNewerThanApp, current, LatestVersion);
        if (current == LatestVersion) return;
        if (current > 0) backup.CreateBeforeMigration($"pre-migration-v{current}");

        foreach (Script script in scripts.Where(item => item.Version > current))
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            connection.Execute(script.Sql, transaction: transaction);
            connection.Execute(
                $"PRAGMA user_version = {script.Version.ToString(CultureInfo.InvariantCulture)};",
                transaction: transaction);
            transaction.Commit();
        }
        connection.Execute("PRAGMA optimize;");
    }

    private static List<Script> LoadEmbedded()
    {
        Assembly assembly = typeof(MigrationRunner).Assembly;
        var scripts = new List<Script>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(static name => name.Contains(".db.", StringComparison.Ordinal)))
        {
            Match match = ScriptName().Match(resource);
            if (!match.Success) continue;
            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Migration resource '{resource}' is missing.");
            using var reader = new StreamReader(stream);
            string sql = UserVersionStatement().Replace(reader.ReadToEnd(), string.Empty);
            scripts.Add(new(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), resource, sql));
        }

        scripts.Sort(static (left, right) => left.Version.CompareTo(right.Version));
        if (scripts.Count != LatestVersion || scripts[^1].Version != LatestVersion ||
            !scripts.Select(static item => item.Version).SequenceEqual(Enumerable.Range(1, LatestVersion)))
            throw new InvalidOperationException("Embedded migrations must be contiguous from 001 through the latest version.");
        return scripts;
    }

    private sealed record Script(int Version, string Name, string Sql);

    [GeneratedRegex(@"\.(\d{3})_[^.]+\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptName();

    [GeneratedRegex(@"^\s*PRAGMA\s+user_version\s*=\s*\d+\s*;[^\r\n]*", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex UserVersionStatement();
}

public static class DatabaseCapabilities
{
    public static void Verify(SqliteConnectionFactory factory)
    {
        using SqliteConnection connection = factory.Open();
        var version = Version.Parse(connection.ExecuteScalar<string>("SELECT sqlite_version();")
            ?? throw new InvalidOperationException("SQLite did not report a version."));
        if (version < new Version(3, 37))
            throw new InvalidOperationException($"SQLite 3.37 or later is required; found {version}.");
        connection.Execute("CREATE VIRTUAL TABLE temp.capability_fts USING fts5(x, tokenize='trigram'); DROP TABLE temp.capability_fts;");
    }
}
