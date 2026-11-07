using System.Text;
using System.Text.RegularExpressions;

namespace GroceryErp.App.Presentation;

/// <summary>
/// One screen's help, read from the small part of Markdown the user guide's help files use: a
/// <c># title</c>, a one-paragraph summary, then <c>## sections</c> of paragraphs, <c>-</c> or
/// <c>1.</c> lists and two-column tables whose first column names fields in <c>**bold**</c>.
/// Images, link targets and <c>&lt;!-- reviewer comments --&gt;</c> are left out, so the same file
/// reads as a guide page with its screenshots and as the in-app help without them.
/// </summary>
internal sealed partial record HelpDocument(string Title, string Summary, IReadOnlyList<HelpSection> Sections)
{
    /// <summary>Gets every table row of every section, in reading order.</summary>
    public IEnumerable<HelpRow> Rows => Sections.SelectMany(static section => section.Rows);

    /// <summary>Reads a help file; a file outside the supported form throws <see cref="FormatException"/>.</summary>
    public static HelpDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        string text = ReviewerComment().Replace(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), "");
        string? title = null;
        string? summary = null;
        var sections = new List<HelpSection>();
        string heading = "";
        var blocks = new List<HelpBlock>();
        var paragraph = new StringBuilder();
        var tableLines = new List<string>();
        var listItems = new List<IReadOnlyList<HelpSpan>>();
        bool listOrdered = false;

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            string line = paragraph.ToString();
            paragraph.Clear();
            if (title is null) throw new FormatException("Help text starts before the # title.");
            // The first paragraph after the title is the summary the ⓘ tooltip shows.
            if (summary is null && sections.Count == 0 && heading.Length == 0 && blocks.Count == 0)
                summary = string.Concat(Inline(line).Select(static span => span.Text));
            else blocks.Add(new HelpParagraph(Inline(line)));
        }

        void FlushTable()
        {
            if (tableLines.Count == 0) return;
            blocks.Add(Table(tableLines));
            tableLines.Clear();
        }

        void FlushList()
        {
            if (listItems.Count == 0) return;
            blocks.Add(new HelpList(listOrdered, listItems.ToArray()));
            listItems.Clear();
        }

        void FlushAll()
        {
            FlushParagraph();
            FlushTable();
            FlushList();
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("![", StringComparison.Ordinal))
            {
                FlushAll();
                continue;
            }
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushAll();
                if (title is not null) throw new FormatException("A help file has one # title.");
                title = line[2..].Trim();
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushAll();
                if (title is null) throw new FormatException("A ## section comes before the # title.");
                if (heading.Length > 0 || blocks.Count > 0) sections.Add(new HelpSection(heading, blocks.ToArray()));
                heading = line[3..].Trim();
                blocks.Clear();
                continue;
            }
            if (line.StartsWith('|'))
            {
                FlushParagraph();
                FlushList();
                tableLines.Add(line);
                continue;
            }
            Match item = ListItem().Match(line);
            if (item.Success)
            {
                FlushParagraph();
                FlushTable();
                bool ordered = item.Groups["number"].Success;
                if (listItems.Count > 0 && ordered != listOrdered) FlushList();
                listOrdered = ordered;
                listItems.Add(Inline(item.Groups["text"].Value));
                continue;
            }
            FlushTable();
            FlushList();
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(line);
        }
        FlushAll();
        if (heading.Length > 0 || blocks.Count > 0) sections.Add(new HelpSection(heading, blocks.ToArray()));
        if (string.IsNullOrWhiteSpace(title)) throw new FormatException("A help file needs a # title.");
        if (string.IsNullOrWhiteSpace(summary)) throw new FormatException("A help file needs a summary paragraph under its title.");
        return new HelpDocument(title, summary, sections);
    }

    /// <summary>
    /// Compares a label as the screen shows it with a bold name in the help: case, spacing and a
    /// trailing menu arrow, ellipsis, colon or required-field star do not matter.
    /// </summary>
    public static bool SameLabel(string? shown, string? written) =>
        shown is not null && written is not null &&
        string.Equals(NormalizeLabel(shown), NormalizeLabel(written), StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeLabel(string label) =>
        Spaces().Replace(label, " ").Trim().TrimEnd('▾', '…', ':', '*', ' ');

    private static HelpTable Table(List<string> lines)
    {
        if (lines.Count < 3 || !TableRule().IsMatch(lines[1]))
            throw new FormatException($"A help table needs a header, a |---|---| rule and rows: {lines[0]}");
        string[] header = Cells(lines[0]);
        var rows = new List<HelpRow>();
        foreach (string line in lines.Skip(2))
        {
            string[] cells = Cells(line);
            rows.Add(new HelpRow(Inline(cells[0]), Inline(cells[1])));
        }
        return new HelpTable(header[0], header[1], rows);
    }

    private static string[] Cells(string line)
    {
        string[] cells = line.Trim().Trim('|').Split('|').Select(static cell => cell.Trim()).ToArray();
        if (cells.Length != 2) throw new FormatException($"A help table has two columns: {line}");
        return cells;
    }

    private static HelpSpan[] Inline(string text)
    {
        string plain = Link().Replace(text, "$1").Replace("`", "", StringComparison.Ordinal);
        string[] parts = plain.Split("**");
        if (parts.Length % 2 == 0) throw new FormatException($"Unmatched ** in help text: {text}");
        return parts.Select(static (part, index) => new HelpSpan(part, index % 2 == 1))
            .Where(static span => span.Text.Length > 0).ToArray();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex ReviewerComment();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^(?:-|(?<number>\d+)\.)\s+(?<text>.+)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\|\s*:?-{3,}:?\s*\|\s*:?-{3,}:?\s*\|$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>A <c>##</c> section of a help file; the part before the first section has no heading.</summary>
internal sealed record HelpSection(string Heading, IReadOnlyList<HelpBlock> Blocks)
{
    public IEnumerable<HelpRow> Rows => Blocks.OfType<HelpTable>().SelectMany(static table => table.Rows);

    /// <summary>
    /// Whether this section is about a tab: its heading is the tab's header, or starts with the
    /// header and a colon, as "Lots: filters" is about the Lots tab.
    /// </summary>
    public bool Covers(string? tab)
    {
        if (tab is null) return false;
        int colon = Heading.IndexOf(':', StringComparison.Ordinal);
        return HelpDocument.SameLabel(tab, Heading) || (colon > 0 && HelpDocument.SameLabel(tab, Heading[..colon]));
    }
}

internal abstract record HelpBlock;

internal sealed record HelpParagraph(IReadOnlyList<HelpSpan> Text) : HelpBlock;

internal sealed record HelpList(bool Ordered, IReadOnlyList<IReadOnlyList<HelpSpan>> Items) : HelpBlock;

internal sealed record HelpTable(string TermHeading, string MeaningHeading, IReadOnlyList<HelpRow> Rows) : HelpBlock;

/// <summary>A field, column or button and what it means.</summary>
internal sealed record HelpRow(IReadOnlyList<HelpSpan> Term, IReadOnlyList<HelpSpan> Meaning)
{
    /// <summary>Gets the bold names in the first column: the labels the screen shows for this row.</summary>
    public IEnumerable<string> Labels => Term.Where(static span => span.Bold).Select(static span => span.Text);

    /// <summary>Gets whether this row describes the field the screen labels <paramref name="label"/>.</summary>
    public bool Describes(string? label) => Labels.Any(written => HelpDocument.SameLabel(label, written));
}

internal readonly record struct HelpSpan(string Text, bool Bold);
