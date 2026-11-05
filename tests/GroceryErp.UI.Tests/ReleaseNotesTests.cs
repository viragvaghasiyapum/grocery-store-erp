using System.Globalization;
using GroceryErp.App;
using GroceryErp.Data.Infrastructure;
using GroceryErp.Data.Services;
using Shouldly;
using Xunit;

namespace GroceryErp.UI.Tests;

// A package shows its release notes once, after sign-in. Both languages must describe the same
// release and the schema this build migrates a store to; 1.1.0's notes ("schema 37") outlived
// schemas 38 and 39 because nothing compared them.
public sealed class ReleaseNotesTests
{
    [Fact]
    public async Task Notes_AreShownOnceInTheOperatorsLanguage_AndStateTheCurrentSchema()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), $"grocery-erp-release-notes-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppPaths(new SettingsBootstrap(root));
            var factory = new SqliteConnectionFactory(paths);
            using var database = new DbExecutor(factory);
            new MigrationRunner(factory, NullMigrationBackup.Instance).Run();
            using var settings = new SettingsService(database, paths, new AuditLog());
            await settings.LoadAsync(ct);
            var service = new ReleaseNotesService(settings);

            ReleaseNotes english = service.GetUnseen(CultureInfo.GetCultureInfo("en-IN")).ShouldNotBeNull();
            ReleaseNotes gujarati = service.GetUnseen(CultureInfo.GetCultureInfo("gu-IN")).ShouldNotBeNull();
            string[] en = Lines(english.Text);
            string[] gu = Lines(gujarati.Text);

            en[0].ShouldMatch(@"^Grocery ERP \d+\.\d+\.\d+$");
            gu[0].ShouldBe(en[0]);
            en[^1].ShouldBe($"Database schema: {MigrationRunner.LatestVersion}.");
            gu[^1].ShouldBe($"ડેટાબેઝ સ્કીમા: {MigrationRunner.LatestVersion}.");
            gu.Count(static line => line.StartsWith('•')).ShouldBe(en.Count(static line => line.StartsWith('•')));
            gu.Length.ShouldBe(en.Length);
            // A reviewer's marker stays in the file; the operator sees only the notes, in English digits.
            foreach (string text in new[] { english.Text, gujarati.Text })
            {
                text.ShouldNotContain("<!--");
                text.ShouldNotContain("needs review");
                text.ShouldNotContain("લોટ");
                text.Any(static c => c is >= '૦' and <= '૯').ShouldBeFalse();
            }

            await service.MarkSeenAsync(english.Version, ct);
            service.GetUnseen(CultureInfo.GetCultureInfo("en-IN")).ShouldBeNull();
            service.GetUnseen(CultureInfo.GetCultureInfo("gu-IN")).ShouldBeNull();
        }
        finally
        {
            SqliteConnectionFactory.ClearPoolForDataRoot(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
