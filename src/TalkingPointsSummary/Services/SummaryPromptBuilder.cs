using System.Globalization;
using System.Text;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Builds the prompt used to generate a weekly summary from news that has not been reported yet.
/// </summary>
/// <remarks>
/// The template file is split into two halves by the <see cref="SystemSectionMarker"/> and
/// <see cref="UserSectionMarker"/> lines. Everything before the user marker is the standing
/// instruction set and is sent as the system prompt: it is byte-identical on every run, so it
/// costs nothing to repeat and is the part a provider can cache. Everything after it is the
/// volatile per-run content and is sent as the user message.
/// </remarks>
public sealed class SummaryPromptBuilder
{
    /// <summary>
    /// Marker that opens the static instruction half of a template.
    /// </summary>
    public const string SystemSectionMarker = "<<<SYSTEM>>>";

    /// <summary>
    /// Marker that opens the volatile per-run half of a template.
    /// </summary>
    public const string UserSectionMarker = "<<<USER>>>";

    /// <summary>
    /// Default per-item character budget applied to <see cref="NewsItem.NewsContent"/>.
    /// </summary>
    /// <remarks>
    /// Scraped newsletter text has no upper bound: a single badly parsed page can carry an entire
    /// site's navigation and archive. Without a per-item cap one such item pushes every other item
    /// out of the model's attention and can blow the context window outright, and because
    /// eligibility is now driven by <see cref="NewsItem.IncludedInSummaryId"/> rather than a date
    /// window, that item stays in the prompt until it is reported.
    /// </remarks>
    public const int DefaultNewsContentCharBudget = 6000;

    /// <summary>
    /// Character budget applied to the most recent digest when it is quoted in full.
    /// </summary>
    public const int LastDigestCharBudget = 16000;

    /// <summary>
    /// Smallest per-item character budget that still leaves room for meaningful content.
    /// </summary>
    public const int MinimumNewsContentCharBudget = 64;

    /// <summary>
    /// Rendered in place of the upcoming dates section when no tracked events are due.
    /// </summary>
    public const string NoUpcomingDatesText =
        "NONE. There are no tracked upcoming events. Omit the "
        + "\"## Important Upcoming Dates\" heading from the digest entirely.";

    private const string TodayToken = "{{TODAY}}";
    private const string SummaryTitleToken = "{{SUMMARY_TITLE}}";
    private const string ContextToken = "{{CONTEXT}}";
    private const string RecentNewsToken = "{{RECENT_NEWS}}";
    private const string CoverageLedgerToken = "{{COVERAGE_LEDGER}}";
    private const string LastDigestToken = "{{LAST_DIGEST}}";
    private const string UpcomingDatesToken = "{{UPCOMING_DATES}}";
    private const string SchoolWideSectionsToken = "{{SCHOOL_WIDE_SECTIONS}}";
    private const string ChildSectionsToken = "{{CHILD_SECTIONS}}";

    private const string LongDateFormat = "dddd, MMMM d, yyyy";

    private static readonly Lazy<string> DefaultTemplate = new(LoadDefaultTemplate);

    private readonly string _template;
    private readonly IGradeCalculator _gradeCalculator;
    private readonly int _newsContentCharBudget;

    /// <summary>
    /// Initializes a prompt builder with the default template and grade calculator.
    /// </summary>
    public SummaryPromptBuilder()
        : this(DefaultTemplate.Value, new GradeCalculator())
    {
    }

    /// <summary>
    /// Initializes a prompt builder with the default template and a supplied grade calculator.
    /// </summary>
    /// <param name="gradeCalculator">Grade calculator used when rendering child context.</param>
    public SummaryPromptBuilder(IGradeCalculator gradeCalculator)
        : this(DefaultTemplate.Value, gradeCalculator)
    {
    }

    /// <summary>
    /// Initializes a prompt builder with a custom template.
    /// </summary>
    /// <param name="template">Template text containing the supported tokens.</param>
    public SummaryPromptBuilder(string template)
        : this(template, new GradeCalculator())
    {
    }

    /// <summary>
    /// Initializes a prompt builder with a custom template and grade calculator.
    /// </summary>
    /// <param name="template">Template text containing the supported tokens.</param>
    /// <param name="gradeCalculator">Grade calculator used when rendering child context.</param>
    /// <param name="newsContentCharBudget">
    /// Per-item character budget applied to each news item's content.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="template"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="newsContentCharBudget"/> is below <see cref="MinimumNewsContentCharBudget"/>.
    /// </exception>
    public SummaryPromptBuilder(
        string template,
        IGradeCalculator gradeCalculator,
        int newsContentCharBudget = DefaultNewsContentCharBudget)
    {
        if (string.IsNullOrWhiteSpace(template))
            throw new ArgumentException("Prompt template cannot be empty.", nameof(template));

        ArgumentOutOfRangeException.ThrowIfLessThan(
            newsContentCharBudget, MinimumNewsContentCharBudget, nameof(newsContentCharBudget));

        var (systemPrompt, userTemplate) = SplitTemplate(template);
        SystemPrompt = systemPrompt;
        _template = userTemplate;
        _gradeCalculator = gradeCalculator;
        _newsContentCharBudget = newsContentCharBudget;
    }

    /// <summary>
    /// The standing instruction set, sent as the system prompt. Empty when the template carries
    /// no <see cref="UserSectionMarker"/>, in which case the whole template is the user message.
    /// </summary>
    public string SystemPrompt { get; }

    /// <summary>
    /// Builds the volatile user message by filling the template with this run's content.
    /// </summary>
    /// <param name="now">Current local date used for prompt tokens and grade labels.</param>
    /// <param name="children">Children included in the summary.</param>
    /// <param name="newsItems">News items that have not been reported in a digest yet.</param>
    /// <param name="priorDigests">Previously sent digests, newest first.</param>
    /// <param name="upcomingEvents">
    /// Active tracked events dated today or later, already filtered by the caller.
    /// </param>
    /// <returns>The user message text, with every supported token replaced.</returns>
    public string Build(
        DateTime now,
        IReadOnlyList<Child> children,
        IReadOnlyList<NewsItem> newsItems,
        IReadOnlyList<PriorDigest> priorDigests,
        IReadOnlyList<TrackedEvent> upcomingEvents)
    {
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(newsItems);
        ArgumentNullException.ThrowIfNull(priorDigests);
        ArgumentNullException.ThrowIfNull(upcomingEvents);

        var today = now.ToString(LongDateFormat, CultureInfo.InvariantCulture);

        var prompt = _template;
        prompt = prompt.Replace(TodayToken, today, StringComparison.Ordinal);
        // The school emoji is written as an escape so this file stays ASCII while the rendered
        // digest title keeps the glyph it has always had.
        prompt = prompt.Replace(SummaryTitleToken, $"# \U0001F3EB School News Digest - {today}", StringComparison.Ordinal);
        prompt = prompt.Replace(ContextToken, BuildContext(children, now), StringComparison.Ordinal);
        prompt = prompt.Replace(RecentNewsToken, BuildRecentNews(newsItems), StringComparison.Ordinal);
        prompt = prompt.Replace(CoverageLedgerToken, SummaryCoverageLedger.Render(priorDigests), StringComparison.Ordinal);
        prompt = prompt.Replace(LastDigestToken, BuildLastDigest(priorDigests), StringComparison.Ordinal);
        prompt = prompt.Replace(UpcomingDatesToken, BuildUpcomingDates(children, upcomingEvents), StringComparison.Ordinal);
        prompt = prompt.Replace(SchoolWideSectionsToken, BuildSchoolWideSections(children), StringComparison.Ordinal);
        prompt = prompt.Replace(ChildSectionsToken, BuildChildSections(children, now), StringComparison.Ordinal);
        return prompt;
    }

    /// <summary>
    /// Renders the "Important Upcoming Dates" section from tracked events, grouped by school and
    /// sorted by date.
    /// </summary>
    /// <param name="children">Children, used to name each school section and order the groups.</param>
    /// <param name="upcomingEvents">Active tracked events dated today or later.</param>
    /// <returns>The rendered section, or <see cref="NoUpcomingDatesText"/> when there are none.</returns>
    /// <remarks>
    /// This section is produced in C# and handed to the model as fixed content to copy, rather
    /// than asked for as output. The dates are already recorded facts: letting the model rebuild
    /// the list from prose is what let events drop out of a digest the week after they were first
    /// announced, and what let a date drift by a day when the source message said "this Thursday".
    /// </remarks>
    internal static string BuildUpcomingDates(
        IReadOnlyList<Child> children,
        IReadOnlyList<TrackedEvent> upcomingEvents)
    {
        if (upcomingEvents.Count == 0)
            return NoUpcomingDatesText;

        // School sections follow the order the children are listed in, so the dates line up with
        // the school-wide sections above them. A school no current child attends still gets a
        // section, sorted after the known ones.
        var schoolOrder = new List<string>();
        var namesBySchool = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var child in children)
        {
            if (namesBySchool.TryGetValue(child.School, out var existing))
            {
                namesBySchool[child.School] = existing + ", " + child.Name;
            }
            else
            {
                schoolOrder.Add(child.School);
                namesBySchool[child.School] = child.Name;
            }
        }

        var orderedGroups = upcomingEvents
            .GroupBy(tracked => tracked.School, StringComparer.Ordinal)
            .OrderBy(bySchool => SchoolRank(schoolOrder, bySchool.Key))
            .ThenBy(bySchool => bySchool.Key, StringComparer.Ordinal);

        var builder = new StringBuilder();

        foreach (var schoolGroup in orderedGroups)
        {
            var heading =
                namesBySchool.TryGetValue(schoolGroup.Key, out var names)
                && !string.IsNullOrWhiteSpace(names)
                    ? $"### {schoolGroup.Key} ({names})"
                    : $"### {schoolGroup.Key}";

            builder.AppendLine(heading);

            var orderedEvents = schoolGroup
                .OrderBy(dated => dated.EventDate)
                .ThenBy(dated => dated.Title, StringComparer.Ordinal);

            foreach (var upcoming in orderedEvents)
            {
                var date = upcoming.EventDate.ToString(LongDateFormat, CultureInfo.InvariantCulture);
                var time = string.IsNullOrWhiteSpace(upcoming.TimeText)
                    ? string.Empty
                    : $" ({upcoming.TimeText.Trim()})";

                builder.AppendLine($"- **{date}** - {upcoming.Title}{time}");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static int SchoolRank(List<string> schoolOrder, string school)
    {
        var index = schoolOrder.IndexOf(school);
        return index < 0 ? int.MaxValue : index;
    }

    private string BuildContext(IEnumerable<Child> children, DateTime now)
    {
        var builder = new StringBuilder();
        foreach (var child in children)
        {
            var gradeLabel = _gradeCalculator.GetCurrentGradeLabel(child, now);
            builder.AppendLine($"- {child.Name} ({child.School}) - {gradeLabel}");
        }

        return builder.ToString().TrimEnd();
    }

    private string BuildRecentNews(IReadOnlyList<NewsItem> newsItems)
    {
        if (newsItems.Count == 0)
            return "None";

        var builder = new StringBuilder();

        for (var index = 0; index < newsItems.Count; index++)
        {
            var item = newsItems[index];
            builder.AppendLine($"### News Item {(index + 1).ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"Student: {item.StudentName}");
            builder.AppendLine($"From: {item.FromName}");
            builder.AppendLine($"Type: {(item.SourceType == SourceType.NewsletterUrl ? "Newsletter" : "Direct Message")}");
            builder.AppendLine($"Date Sent: {item.SentAt:O}");
            builder.AppendLine($"Content: {ApplyCharBudget(item.NewsContent, _newsContentCharBudget, "news item")}");
            builder.AppendLine("---");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildLastDigest(IReadOnlyList<PriorDigest> priorDigests)
    {
        if (priorDigests.Count == 0)
            return "None";

        var latest = priorDigests.MaxBy(digest => digest.LocalDate)!;
        var sentOn = latest.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var content = ApplyCharBudget(latest.Content, LastDigestCharBudget, "digest");

        return $"Sent {sentOn}:{Environment.NewLine}{content}";
    }

    /// <summary>
    /// Caps one block of text at a character budget, stating plainly how much was dropped so the
    /// model does not read a mid-sentence stop as the end of the source.
    /// </summary>
    private static string ApplyCharBudget(string? content, int budget, string label)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= budget)
            return content ?? string.Empty;

        var omitted = (content.Length - budget).ToString(CultureInfo.InvariantCulture);
        var total = content.Length.ToString(CultureInfo.InvariantCulture);

        return content[..budget]
            + Environment.NewLine
            + $"[... truncated: {omitted} of {total} characters omitted from this {label} ...]";
    }

    private string BuildChildSections(IEnumerable<Child> children, DateTime now)
    {
        var builder = new StringBuilder();

        foreach (var child in children)
        {
            var gradeLabel = _gradeCalculator.GetCurrentGradeLabel(child, now);
            builder.AppendLine($"# {child.Emoji} {child.Name} ({gradeLabel} at {child.School})");
            builder.AppendLine("### [Subheading Topic]");
            builder.AppendLine("[Detailed but scannable summary. Preserve classroom learning, reminders, celebrations, and next steps when present.]\n");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildSchoolWideSections(IEnumerable<Child> children)
    {
        var builder = new StringBuilder();

        foreach (var schoolGroup in children.GroupBy(child => child.School, StringComparer.Ordinal))
        {
            builder.AppendLine($"## {schoolGroup.Key} School-Wide News");
            builder.AppendLine("(Include only if there is school-wide news for this school this week.)");
            builder.AppendLine("### [Subheading Topic]");
            builder.AppendLine("[School-wide summary for this school only.]\n");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Splits a template into its system half and its user half. A template with no user marker
    /// is treated as a user message in full, which keeps single-token templates usable in tests.
    /// </summary>
    private static (string SystemPrompt, string UserTemplate) SplitTemplate(string template)
    {
        var userIndex = template.IndexOf(UserSectionMarker, StringComparison.Ordinal);
        if (userIndex < 0)
            return (string.Empty, template.Trim());

        var head = template[..userIndex];
        var systemIndex = head.IndexOf(SystemSectionMarker, StringComparison.Ordinal);
        var systemPrompt = systemIndex < 0
            ? head
            : head[(systemIndex + SystemSectionMarker.Length)..];

        var userTemplate = template[(userIndex + UserSectionMarker.Length)..];

        return (systemPrompt.Trim(), userTemplate.Trim());
    }

    private static string LoadDefaultTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "SummaryPromptTemplate.txt");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Summary prompt template not found at '{path}'.", path);

        return File.ReadAllText(path);
    }
}
