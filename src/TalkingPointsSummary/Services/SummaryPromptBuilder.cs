using System.Globalization;
using System.Text;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Builds the Anthropic prompt used to generate a weekly summary from recent news.
/// </summary>
public sealed class SummaryPromptBuilder
{
    private const string TodayToken = "{{TODAY}}";
    private const string SummaryTitleToken = "{{SUMMARY_TITLE}}";
    private const string WeekCalendarToken = "{{WEEK_CALENDAR}}";
    private const string ContextToken = "{{CONTEXT}}";
    private const string RecentNewsToken = "{{RECENT_NEWS}}";
    private const string PreviousSummariesToken = "{{PREVIOUS_SUMMARIES}}";
    private const string SchoolWideSectionsToken = "{{SCHOOL_WIDE_SECTIONS}}";
    private const string ChildSectionsToken = "{{CHILD_SECTIONS}}";
    private const string SchoolDateSectionsToken = "{{SCHOOL_DATE_SECTIONS}}";

    private static readonly Lazy<string> DefaultTemplate = new(LoadDefaultTemplate);

    private readonly string _template;
    private readonly IGradeCalculator _gradeCalculator;

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
    public SummaryPromptBuilder(string template, IGradeCalculator gradeCalculator)
    {
        _template = string.IsNullOrWhiteSpace(template)
            ? throw new ArgumentException("Prompt template cannot be empty.", nameof(template))
            : template;
        _gradeCalculator = gradeCalculator;
    }

    /// <summary>
    /// Builds the final prompt text by filling the template with current context and recent content.
    /// </summary>
    /// <param name="now">Current date used for prompt tokens and grade labels.</param>
    /// <param name="children">Children included in the summary.</param>
    /// <param name="newsItems">Recent news items available for summarization.</param>
    /// <param name="previousSummaries">Recent summaries used as historical context.</param>
    public string Build(
        DateTime now,
        List<Child> children,
        List<NewsItem> newsItems,
        List<Summary> previousSummaries)
    {
        var prompt = _template;
        prompt = prompt.Replace(TodayToken, now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        prompt = prompt.Replace(SummaryTitleToken, $"# 🏫 School News Digest — {now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)}", StringComparison.Ordinal);
        prompt = prompt.Replace(WeekCalendarToken, BuildDateReferenceCalendar(now), StringComparison.Ordinal);
        prompt = prompt.Replace(ContextToken, BuildContext(children, now), StringComparison.Ordinal);
        prompt = prompt.Replace(RecentNewsToken, BuildRecentNews(newsItems), StringComparison.Ordinal);
        prompt = prompt.Replace(PreviousSummariesToken, BuildPreviousSummaries(previousSummaries), StringComparison.Ordinal);
        prompt = prompt.Replace(SchoolWideSectionsToken, BuildSchoolWideSections(children), StringComparison.Ordinal);
        prompt = prompt.Replace(ChildSectionsToken, BuildChildSections(children, now), StringComparison.Ordinal);
        prompt = prompt.Replace(SchoolDateSectionsToken, BuildSchoolDateSections(children), StringComparison.Ordinal);
        return prompt;
    }

    private static string BuildDateReferenceCalendar(DateTime now)
    {
        var builder = new StringBuilder();
        for (var i = -7; i <= 14; i++)
        {
            var date = now.AddDays(i);
            var formatted = date.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);
            builder.AppendLine($"     - {formatted}");
        }
        return builder.ToString().TrimEnd();
    }

    private string BuildContext(IEnumerable<Child> children, DateTime now)
    {
        var builder = new StringBuilder();
        foreach (var child in children)
        {
            var gradeLabel = _gradeCalculator.GetCurrentGradeLabel(child, now);
            builder.AppendLine($"- {child.Name} ({child.School}) — {gradeLabel}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildRecentNews(IReadOnlyList<NewsItem> newsItems)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < newsItems.Count; index++)
        {
            var item = newsItems[index];
            builder.AppendLine($"### News Item {index + 1}");
            builder.AppendLine($"Student: {item.StudentName}");
            builder.AppendLine($"From: {item.FromName}");
            builder.AppendLine($"Type: {(item.SourceType == SourceType.NewsletterUrl ? "Newsletter" : "Direct Message")}");
            builder.AppendLine($"Date Sent: {item.SentAt:O}");
            builder.AppendLine($"Content: {item.NewsContent}");
            builder.AppendLine("---");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildPreviousSummaries(IReadOnlyList<Summary> previousSummaries)
    {
        if (previousSummaries.Count == 0)
            return "None";

        var builder = new StringBuilder();
        foreach (var summary in previousSummaries)
        {
            builder.AppendLine(summary.Content);
            builder.AppendLine("---");
        }

        return builder.ToString().TrimEnd();
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

        foreach (var schoolGroup in children.GroupBy(child => child.School))
        {
            builder.AppendLine($"## {schoolGroup.Key} School-Wide News");
            builder.AppendLine("(Include only if there is school-wide news for this school this week.)");
            builder.AppendLine("### [Subheading Topic]");
            builder.AppendLine("[School-wide summary for this school only.]\n");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildSchoolDateSections(IEnumerable<Child> children)
    {
        var builder = new StringBuilder();

        foreach (var schoolGroup in children.GroupBy(child => child.School))
        {
            var childNames = string.Join(", ", schoolGroup.Select(child => child.Name));
            builder.AppendLine($"### {schoolGroup.Key} ({childNames})");
            builder.AppendLine("- **[Date]** – [Event] ([Time if applicable])");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string LoadDefaultTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "SummaryPromptTemplate.txt");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Summary prompt template not found at '{path}'.", path);

        return File.ReadAllText(path);
    }
}