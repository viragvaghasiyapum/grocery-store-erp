using System.Text.Json;
using GroceryErp.Core.Errors;

namespace GroceryErp.Data.Infrastructure;

public interface ISettingsBootstrap
{
    string? DataRoot { get; }
}

public sealed record SettingsBootstrap(string? DataRoot) : ISettingsBootstrap
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GroceryErp.boot.json");

    public static SettingsBootstrap Load(string? filePath = null)
    {
        string path = filePath ?? FilePath;
        if (!File.Exists(path)) return new SettingsBootstrap((string?)null);
        try
        {
            return JsonSerializer.Deserialize<SettingsBootstrap>(File.ReadAllText(path)) ?? new SettingsBootstrap((string?)null);
        }
        catch (JsonException exception)
        {
            throw new DomainException(ErrorCode.Validation, "Invalid bootstrap file", exception.Message);
        }
    }

    public static void Save(string dataRoot, string? filePath = null)
    {
        string path = filePath ?? FilePath;
        string fullRoot = Path.GetFullPath(dataRoot);
        AppPaths.RejectCloudSynchronizedPath(fullRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new ArgumentException("Invalid bootstrap path."));
        string partial = path + ".partial";
        File.WriteAllText(partial, JsonSerializer.Serialize(new SettingsBootstrap(fullRoot)));
        File.Move(partial, path, overwrite: true);
    }
}

public sealed class AppPaths
{
    public AppPaths(ISettingsBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        Root = Path.GetFullPath(bootstrap.DataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GroceryErpData"));
        RejectCloudSynchronizedPath(Root);
        Directory.CreateDirectory(DbDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ExportsDir);
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(DefaultBackupDir);
    }

    public string Root { get; }
    public string DbDir => Path.Combine(Root, "db");
    public string DatabaseFile => Path.Combine(DbDir, "grocery.db");
    public string LogsDir => Path.Combine(Root, "logs");
    public string ExportsDir => Path.Combine(Root, "exports");
    public string TempDir => Path.Combine(Root, "temp");
    public string DefaultBackupDir => Path.Combine(Root, "backups");

    public static void RejectCloudSynchronizedPath(string path)
    {
        string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (string.IsNullOrWhiteSpace(oneDrive)) return;
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string cloudRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oneDrive));
        if (candidate.Equals(cloudRoot, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(cloudRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new DomainException(ErrorCode.Validation, "The data folder cannot be inside OneDrive.");
    }
}
