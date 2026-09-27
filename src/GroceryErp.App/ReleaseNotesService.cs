using System.Globalization;
using System.IO;
using System.Reflection;
using GroceryErp.Data.Services;

namespace GroceryErp.App;

public sealed record ReleaseNotes(string Version, string Markdown);

public sealed class ReleaseNotesService(SettingsService settings)
{
    public ReleaseNotes? GetUnseen(CultureInfo culture)
    {
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
        if (string.Equals(settings.Current.Values.GetValueOrDefault("app.last_seen_version"), version,
                StringComparison.Ordinal)) return null;
        string language = string.Equals(culture.TwoLetterISOLanguageName, "gu", StringComparison.Ordinal) ? "gu" : "en";
        Assembly assembly = typeof(ReleaseNotesService).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream($"GroceryErp.App.CHANGELOG.{language}.md");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return new(version, reader.ReadToEnd());
    }

    public Task MarkSeenAsync(string version, CancellationToken cancellationToken = default) =>
        settings.SaveAsync(new Dictionary<string, string> { ["app.last_seen_version"] = version }, cancellationToken);
}
