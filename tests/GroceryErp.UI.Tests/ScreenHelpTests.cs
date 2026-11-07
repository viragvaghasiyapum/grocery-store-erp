using System.Collections;
using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.RegularExpressions;
using GroceryErp.App.Presentation;
using GroceryErp.Localization;
using Shouldly;
using Xunit;

namespace GroceryErp.UI.Tests;

// A screen's help is a file of the user guide that the app embeds. Each screen that names a help
// topic needs that file in English and Gujarati, both files need the same shape, and every field
// the help names in bold must be a label the app shows in that language, so renaming a label on
// screen fails here until its help says the same.
public sealed partial class ScreenHelpTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-IN");
    private static readonly CultureInfo Gujarati = CultureInfo.GetCultureInfo("gu-IN");

    [Fact]
    public void EveryScreenWithHelp_HasHelpInBothLanguages_ThatNamesItsLabels()
    {
        string root = RepositoryRoot();
        string presentation = Path.Combine(root, "src", "GroceryErp.App", "Presentation");
        string[] views = Directory.GetFiles(presentation, "*.xaml");
        // A view names its topic in XAML; a window built in code, or one that serves two purposes,
        // names it with ScreenHelp.SetTopic, where every quoted name is a topic.
        IEnumerable<string> codeTopics = Directory.GetFiles(presentation, "*.cs")
            .SelectMany(file => TopicCall().Matches(File.ReadAllText(file)))
            .SelectMany(call => QuotedName().Matches(call.Groups[1].Value)).Select(static name => name.Groups[1].Value);
        string[] topics = views.SelectMany(file => TopicAttribute().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups[1].Value).Concat(codeTopics)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        topics.ShouldNotBeEmpty();
        string help = Path.Combine(root, "docs", "user-guide", "help");
        foreach (string language in new[] { "en", "gu" })
            Directory.GetFiles(Path.Combine(help, language), "*.md").Select(Path.GetFileNameWithoutExtension)
                .Order(StringComparer.Ordinal).ShouldBe(topics, ignoreOrder: false,
                    customMessage: $"{language} help files and screens with a help topic");

        HashSet<string> englishLabels = Labels(CultureInfo.InvariantCulture, views);
        HashSet<string> gujaratiLabels = Labels(Gujarati, views);
        foreach (string topic in topics)
        {
            HelpDocument english = ScreenHelp.Read(topic, English).ShouldNotBeNull($"{topic}: English help is embedded");
            HelpDocument gujarati = ScreenHelp.Read(topic, Gujarati).ShouldNotBeNull($"{topic}: Gujarati help is embedded");
            Shape(gujarati).ShouldBe(Shape(english), $"{topic}: the Gujarati help has the English sections, tables, rows and lists");
            foreach ((HelpDocument document, HashSet<string> labels, string language) in
                     new[] { (english, englishLabels, "en"), (gujarati, gujaratiLabels, "gu") })
            {
                foreach (string label in document.Rows.SelectMany(static row => row.Labels))
                    labels.ShouldContain(HelpDocument.NormalizeLabel(label),
                        $"{language}/{topic}.md names \"{label}\", which no screen shows");
                string text = File.ReadAllText(Path.Combine(help, language, topic + ".md"));
                text.Any(static c => c is >= '૦' and <= '૯').ShouldBeFalse($"{language}/{topic}.md uses English digits");
                text.ShouldNotContain("લોટ");
            }
        }
    }

    [Fact]
    public void Parse_ReadsTheGuideSubset_AndLeavesOutImagesLinksAndComments()
    {
        HelpDocument document = HelpDocument.Parse("""
            <!-- MT – needs review -->
            # Products

            Find and change
            products.

            ![Products](../../screenshots/products.en.png)

            A note with a [link](other.md).

            ## Filters

            | Field | What it means |
            |---|---|
            | **Stock**, **Expiry ▾** | **Low stock** lists products at their reorder level. |
            | **Clear filters** (Ctrl+L) | Resets them. |

            - one
            - two

            1. first
            2. second
            """);

        document.Title.ShouldBe("Products");
        document.Summary.ShouldBe("Find and change products.");
        document.Sections.Count.ShouldBe(2);
        document.Sections[0].Heading.ShouldBe("");
        ((HelpParagraph)document.Sections[0].Blocks.Single()).Text.Single().Text.ShouldBe("A note with a link.");
        document.Sections[1].Heading.ShouldBe("Filters");
        document.Sections[1].Blocks.Select(static block => block.GetType().Name)
            .ShouldBe(["HelpTable", "HelpList", "HelpList"]);
        HelpRow[] rows = [.. document.Rows];
        rows[0].Labels.ShouldBe(["Stock", "Expiry ▾"]);
        rows[0].Describes("expiry").ShouldBeTrue();
        rows[0].Describes("Low stock").ShouldBeFalse();
        rows[1].Describes("Clear filters:").ShouldBeTrue();
        ((HelpList)document.Sections[1].Blocks[2]).Ordered.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Lots", "Lots", true)]
    [InlineData("Lots: filters", "lots", true)]
    [InlineData("Lots: filters", "Lots:", true)]
    [InlineData("Lots: filters", "Lots: filters", true)]
    [InlineData("Lots: filters", "Lot", false)]
    [InlineData("Lots filters", "Lots", false)]
    [InlineData("Lots", null, false)]
    public void Section_CoversTheTabItsHeadingNames(string heading, string? tab, bool covers) =>
        new HelpSection(heading, []).Covers(tab).ShouldBe(covers);

    [Theory]
    [InlineData("Products\n\nNo title.")]
    [InlineData("# Products\n\n## Filters\n\nNo summary above.")]
    [InlineData("# Products\n\nSummary.\n\n| A | B | C |\n|---|---|---|\n| x | y | z |")]
    [InlineData("# Products\n\nSummary with **unmatched bold.")]
    public void Parse_RejectsTextOutsideTheSubset(string markdown) =>
        Should.Throw<FormatException>(() => HelpDocument.Parse(markdown));

    // Every text the app can show in this language, plus the labels views write out, such as "GSTIN".
    private static HashSet<string> Labels(CultureInfo culture, IEnumerable<string> views)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ResourceSet strings = SharedStrings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: true)!;
        foreach (DictionaryEntry entry in strings)
            if (entry.Value is string text) labels.Add(HelpDocument.NormalizeLabel(text));
        foreach (string view in views)
            foreach (Match match in LiteralLabel().Matches(File.ReadAllText(view)))
                labels.Add(HelpDocument.NormalizeLabel(WebUtility.HtmlDecode(match.Groups[1].Value)));
        return labels;
    }

    private static string Shape(HelpDocument document) => string.Join(" / ", document.Sections.Select(static section =>
        string.Join(",", section.Blocks.Select(static block => block switch
        {
            HelpTable table => $"table {table.Rows.Count}",
            HelpList list => $"list {list.Items.Count}",
            _ => "text",
        }))));

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "GroceryErp.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    [GeneratedRegex(@"ScreenHelp\.Topic=""([^""]+)""")]
    private static partial Regex TopicAttribute();

    [GeneratedRegex(@"ScreenHelp\.SetTopic\(([^;]*)\);")]
    private static partial Regex TopicCall();

    [GeneratedRegex(@"""([a-z][a-z0-9-]*)""")]
    private static partial Regex QuotedName();

    [GeneratedRegex(@"(?:Header|Content|Text|Label|PlaceholderText|AutomationProperties\.Name)=""([^""{][^""]*)""")]
    private static partial Regex LiteralLabel();
}
