using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using GroceryErp.Data.Services;

namespace GroceryErp.App;

public sealed record ReleaseNotes(string Version, string Text);

public sealed partial class ReleaseNotesService(SettingsService settings)
{
    public ReleaseNotes? GetUnseen(CultureInfo culture)
    {
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
        if (string.Equals(settings.Current.Values.GetValueOrDefault("app.last_seen_version"), version,
                StringComparison.Ordinal)) return null;
        return Read(culture) is { } text ? new(version, text) : null;
    }

    public Task MarkSeenAsync(string version, CancellationToken cancellationToken = default) =>
        settings.SaveAsync(new Dictionary<string, string> { ["app.last_seen_version"] = version }, cancellationToken);

    /// <summary>The notes in the culture's language, as the operator sees them.</summary>
    internal static string? Read(CultureInfo culture)
    {
        string language = string.Equals(culture.TwoLetterISOLanguageName, "gu", StringComparison.Ordinal) ? "gu" : "en";
        // The csproj marks both files WithCulture="false", so they stay in this assembly.
        using Stream? stream = typeof(ReleaseNotesService).Assembly
            .GetManifestResourceStream($"GroceryErp.App.CHANGELOG.{language}.md");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        // A notes file may carry a reviewer's marker, such as "<!-- MT – needs review -->"; the
        // operator sees only the notes.
        return ReviewerComment().Replace(reader.ReadToEnd(), "").Trim();
    }

    [GeneratedRegex(@"<!--.*?-->\r?\n?", RegexOptions.Singleline)]
    private static partial Regex ReviewerComment();
}
