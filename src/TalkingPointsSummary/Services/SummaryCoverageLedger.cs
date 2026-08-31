using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TalkingPointsSummary.Services;

/// <summary>
/// A previously sent digest reduced to the two facts the coverage index needs: the local
/// calendar date it went out on, and its markdown body.
/// </summary>
/// <param name="LocalDate">
/// Local calendar date the digest was generated and sent on. The caller converts from UTC,
/// because a digest generated late on a Sunday evening belongs to that Sunday, not to the
/// Monday its UTC timestamp falls in.
/// </param>
/// <param name="Content">Full markdown content of the digest.</param>
public sealed record PriorDigest(DateTime LocalDate, string Content);

/// <summary>
/// The topics and dated lines lifted out of a single prior digest.
/// </summary>
/// <param name="Topics">
/// Subheading topics the digest reported, in document order, each prefixed with the section
/// heading it sat under when there was one.
/// </param>
/// <param name="DatedItems">Dated bullet lines the digest listed, in document order.</param>
public sealed record SummaryCoverageEntry(
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> DatedItems);

/// <summary>
/// Builds a compact, dated index of what earlier digests already covered.
/// </summary>
/// <remarks>
/// Prior digests used to be concatenated into the prompt in full and undated, separated only by
/// a horizontal rule. The model could not tell last week's digest from one sent five weeks ago,
/// so follow-ups read as repeats and the prompt grew without bound across a school year. This
/// ledger replaces that dump: every prior digest contributes a dated line listing the topics it
/// covered and the dates it announced, and only the most recent one is still shown in full
/// (by the prompt builder) so the model has a voice to match.
/// <para>
/// Extraction is deliberately done in C# against the markdown the digest template produces,
/// not by asking a model to summarize its own history: the structure is fixed and known, so a
/// regex reads it exactly and for free.
/// </para>
/// </remarks>
public static partial class SummaryCoverageLedger
{
    /// <summary>
    /// Rendered when there are no prior digests at all.
    /// </summary>
    public const string EmptyLedgerText = "None";

    /// <summary>
    /// Rendered for a digest that contributed no topics, or no dated lines.
    /// </summary>
    public const string EmptyListText = "(none)";

    /// <summary>
    /// Maximum number of prior digests rendered into the index.
    /// </summary>
    public const int MaxDigests = 20;

    /// <summary>
    /// Maximum number of topics listed for any one digest before the rest are counted off.
    /// </summary>
    public const int MaxTopicsPerDigest = 20;

    /// <summary>
    /// Maximum number of dated lines listed for any one digest before the rest are counted off.
    /// </summary>
    public const int MaxDatedItemsPerDigest = 20;

    /// <summary>
    /// Maximum length of any single topic or dated line in the index. Longer entries are cut
    /// with a trailing ellipsis so one runaway heading cannot dominate the index.
    /// </summary>
    public const int MaxEntryLength = 140;

    /// <summary>
    /// Heading text that starts the digest's dates section. Subheadings inside it name schools,
    /// not subjects, and the dates under them are already captured as dated lines, so they are
    /// not listed again as topics.
    /// </summary>
    private const string UpcomingDatesHeadingText = "important upcoming dates";

    private const string Separator = " | ";
    private const string SectionSeparator = " > ";
    private const string Ellipsis = "...";

    /// <summary>
    /// Renders the dated coverage index for a set of prior digests.
    /// </summary>
    /// <param name="digests">
    /// Prior digests, newest first. At most <see cref="MaxDigests"/> are rendered.
    /// </param>
    /// <returns>
    /// The index text, or <see cref="EmptyLedgerText"/> when there are no prior digests.
    /// </returns>
    public static string Render(IReadOnlyList<PriorDigest> digests)
    {
        if (digests is null || digests.Count == 0)
            return EmptyLedgerText;

        var builder = new StringBuilder();
        var rendered = 0;

        foreach (var digest in digests)
        {
            if (rendered == MaxDigests)
                break;

            var entry = Extract(digest.Content);

            // The line break is written explicitly rather than through AppendLine so the index
            // is byte-identical regardless of which machine builds the prompt.
            builder.Append(digest.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            builder.Append(" digest:\n");
            builder.Append("  topics: ").Append(JoinCapped(entry.Topics, MaxTopicsPerDigest)).Append('\n');
            builder.Append("  dates listed: ").Append(JoinCapped(entry.DatedItems, MaxDatedItemsPerDigest)).Append('\n');

            rendered++;
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Extracts the topics and dated lines from one digest's markdown.
    /// </summary>
    /// <param name="content">Markdown body of a previously sent digest.</param>
    /// <returns>The topics and dated lines found, in document order.</returns>
    /// <remarks>
    /// Level 1 and level 2 headings are the digest's structure (the title, the district band,
    /// each school, each child), so they are kept as the section a topic belongs to rather than
    /// listed as topics themselves. Level 3 and deeper headings are the actual subjects the
    /// digest reported, and those are what the index lists.
    /// </remarks>
    public static SummaryCoverageEntry Extract(string? content)
    {
        var topics = new List<string>();
        var datedItems = new List<string>();

        if (string.IsNullOrWhiteSpace(content))
            return new SummaryCoverageEntry(topics, datedItems);

        var seenTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        var inUpcomingDatesSection = false;

        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        foreach (var line in lines)
        {
            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                var title = CollapseWhitespace(heading.Groups["title"].Value);
                if (title.Length == 0)
                    continue;

                if (heading.Groups["hashes"].Value.Length <= 2)
                {
                    section = title;
                    inUpcomingDatesSection = title.Contains(
                        UpcomingDatesHeadingText, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inUpcomingDatesSection)
                    continue;

                var topic = section.Length == 0 ? title : section + SectionSeparator + title;
                topic = Shorten(topic, MaxEntryLength);
                if (seenTopics.Add(topic))
                    topics.Add(topic);

                continue;
            }

            var dated = DatedBulletPattern().Match(line);
            if (!dated.Success)
                continue;

            var date = CollapseWhitespace(dated.Groups["date"].Value);
            if (date.Length == 0)
                continue;

            var text = CollapseWhitespace(dated.Groups["event"].Value);
            var item = Shorten(text.Length == 0 ? date : date + " - " + text, MaxEntryLength);
            if (seenDates.Add(item))
                datedItems.Add(item);
        }

        return new SummaryCoverageEntry(topics, datedItems);
    }

    private static string JoinCapped(IReadOnlyList<string> values, int cap)
    {
        if (values.Count == 0)
            return EmptyListText;

        if (values.Count <= cap)
            return string.Join(Separator, values);

        var overflow = (values.Count - cap).ToString(CultureInfo.InvariantCulture);
        return string.Join(Separator, values.Take(cap)) + Separator + "(+" + overflow + " more)";
    }

    private static string Shorten(string value, int max)
        => value.Length <= max ? value : value[..(max - Ellipsis.Length)] + Ellipsis;

    private static string CollapseWhitespace(string value)
        => WhitespaceRunPattern().Replace(value, " ").Trim();

    /// <summary>
    /// Matches an ATX markdown heading, capturing its level and title. The optional trailing
    /// run of hashes is the closing sequence markdown allows and is not part of the title.
    /// </summary>
    [GeneratedRegex(@"^[ ]{0,3}(?<hashes>#{1,6})[ \t]+(?<title>.+?)[ \t]*#*[ \t]*$")]
    private static partial Regex HeadingPattern();

    /// <summary>
    /// Matches a dated bullet line as the digest template writes it, for example
    /// "- **Friday, May 15, 2026** - Field Day (9:00 AM)". The separator between the bold date
    /// and the event is optional and may be a hyphen, an en dash (U+2013), an em dash (U+2014), or a colon,
    /// because digests generated before this format was pinned down used all of them.
    /// </summary>
    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+\*\*(?<date>[^*]+?)\*\*[ \t]*(?:[-:\u2013\u2014][ \t]*)?(?<event>.*?)[ \t]*$")]
    private static partial Regex DatedBulletPattern();

    /// <summary>
    /// Matches any run of whitespace, used to flatten a heading or bullet onto one line.
    /// </summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunPattern();
}
