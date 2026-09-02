using System.Globalization;
using System.Text;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Builds the AI prompt used to critique a generated weekly digest against its sources.
/// </summary>
public sealed class SummaryCritiquePromptBuilder
{
    private const string DateReferenceToken = "{{DATE_REFERENCE}}";
    private const string SourceItemsToken = "{{SOURCE_ITEMS}}";
    private const string ActiveEventsToken = "{{ACTIVE_EVENTS}}";
    private const string CoverageLedgerToken = "{{COVERAGE_LEDGER}}";
    private const string DraftToken = "{{DRAFT}}";

    /// <summary>
    /// Text substituted for an input the caller had nothing to supply for.
    /// </summary>
    private const string EmptyPlaceholder = "None";

    /// <summary>
    /// Days of calendar shown before the earliest date the critic has to reason about.
    /// </summary>
    private const int CalendarDaysBefore = 7;

    /// <summary>
    /// Days of calendar shown after the latest date the critic has to reason about. A digest
    /// announces events months out, so the window has to outrun the news it was built from.
    /// </summary>
    private const int CalendarDaysAfter = 120;

    /// <summary>
    /// How far before today the calendar will start, however old the oldest source item is. The
    /// window is derived from the source items, and one stale item would otherwise render years of
    /// dates into the prompt. Clamping the ends rather than truncating the range keeps today and
    /// the months after it in the calendar, which is the part the draft's dates live in.
    /// </summary>
    private const int MaxCalendarLookbackDays = 120;

    /// <summary>
    /// How far after today a source item may push the calendar out, before clamping.
    /// </summary>
    private const int MaxCalendarLookaheadDays = 120;

    /// <summary>
    /// Per-item character budget applied to each source item's content.
    /// </summary>
    /// <remarks>
    /// Deliberately the same budget the digest prompt applies, because the critic has to review the
    /// draft against what the generator was actually shown. Sending the full unbounded scrape here
    /// would make the review request larger than the request it reviews, so the critic would fail
    /// on exactly the biggest runs, and every failure is swallowed into "no findings": the digest
    /// would then go out unreviewed precisely when it most needs reviewing.
    /// </remarks>
    public const int MaxSourceContentChars = SummaryPromptBuilder.DefaultNewsContentCharBudget;

    private const string TruncationNoticeFormat =
        "[... truncated: {0} of {1} characters omitted from this source item ...]";

    private static readonly Lazy<string> DefaultTemplate = new(LoadDefaultTemplate);

    private readonly string _template;

    /// <summary>
    /// Initializes a prompt builder with the default critique template.
    /// </summary>
    public SummaryCritiquePromptBuilder()
        : this(DefaultTemplate.Value)
    {
    }

    /// <summary>
    /// Initializes a prompt builder with a custom template.
    /// </summary>
    /// <param name="template">Template text containing the supported tokens.</param>
    public SummaryCritiquePromptBuilder(string template)
    {
        _template = string.IsNullOrWhiteSpace(template)
            ? throw new ArgumentException("Prompt template cannot be empty.", nameof(template))
            : template;
    }

    /// <summary>
    /// Builds the critique prompt for one draft digest.
    /// </summary>
    /// <remarks>
    /// Each source item is stamped with its send date read in <paramref name="timeZone"/>, not in
    /// UTC. A newsletter sent at 19:00 local is already the next day in UTC, and a critic told the
    /// wrong send date would resolve every relative phrase in that item one day late and then
    /// report the correct draft as wrong.
    /// </remarks>
    /// <param name="sourceItems">News items the digest was generated from.</param>
    /// <param name="activeEvents">Rendered active-events list, or null when there is none.</param>
    /// <param name="coverageLedger">Rendered coverage ledger, or null when there is none.</param>
    /// <param name="draftMarkdown">Draft digest markdown under review.</param>
    /// <param name="nowLocal">Current local date, used to bound the reference calendar.</param>
    /// <param name="timeZone">Timezone the school and its families read dates in.</param>
    public string Build(
        IReadOnlyList<NewsItem> sourceItems,
        string? activeEvents,
        string? coverageLedger,
        string draftMarkdown,
        DateTime nowLocal,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(draftMarkdown);
        ArgumentNullException.ThrowIfNull(timeZone);

        var localDates = sourceItems
            .Select(item => ToLocalDate(item.SentAt, timeZone))
            .ToList();

        var prompt = _template;
        prompt = prompt.Replace(
            DateReferenceToken,
            BuildDateReferenceCalendar(localDates, nowLocal.Date),
            StringComparison.Ordinal);
        prompt = prompt.Replace(
            SourceItemsToken,
            BuildSourceItems(sourceItems, localDates),
            StringComparison.Ordinal);
        prompt = prompt.Replace(ActiveEventsToken, Placeholder(activeEvents), StringComparison.Ordinal);
        prompt = prompt.Replace(CoverageLedgerToken, Placeholder(coverageLedger), StringComparison.Ordinal);
        prompt = prompt.Replace(DraftToken, draftMarkdown, StringComparison.Ordinal);
        return prompt;
    }

    /// <summary>
    /// Formats a date the way the prompt and its reference calendar render dates.
    /// </summary>
    /// <param name="date">Date to format.</param>
    public static string FormatDate(DateTime date)
        => date.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a date in the machine-readable form the critic is asked to answer in.
    /// </summary>
    /// <param name="date">Date to format.</param>
    public static string FormatIsoDate(DateTime date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime ToLocalDate(DateTime sentAt, TimeZoneInfo timeZone)
    {
        var sentUtc = DateTime.SpecifyKind(sentAt, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(sentUtc, timeZone).Date;
    }

    private static string Placeholder(string? value)
        => string.IsNullOrWhiteSpace(value) ? EmptyPlaceholder : value.Trim();

    /// <summary>
    /// Renders the calendar the critic converts relative phrases with. It spans every date the
    /// critic has to reason about: the oldest source item it must resolve phrases against, today,
    /// and the months of upcoming dates a digest announces.
    /// </summary>
    private static string BuildDateReferenceCalendar(IReadOnlyList<DateTime> localDates, DateTime today)
    {
        var earliest = today;
        var latest = today;

        foreach (var date in localDates)
        {
            if (date < earliest)
                earliest = date;
            if (date > latest)
                latest = date;
        }

        var floor = today.AddDays(-MaxCalendarLookbackDays);
        var ceiling = today.AddDays(MaxCalendarLookaheadDays);

        if (earliest < floor)
            earliest = floor;
        if (latest > ceiling)
            latest = ceiling;

        var start = earliest.AddDays(-CalendarDaysBefore);
        var end = latest.AddDays(CalendarDaysAfter);

        var builder = new StringBuilder();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            builder.AppendLine($"- {FormatIsoDate(date)} = {FormatDate(date)}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildSourceItems(IReadOnlyList<NewsItem> sourceItems, IReadOnlyList<DateTime> localDates)
    {
        if (sourceItems.Count == 0)
            return EmptyPlaceholder;

        var builder = new StringBuilder();

        for (var index = 0; index < sourceItems.Count; index++)
        {
            var item = sourceItems[index];
            var sentDate = localDates[index];

            var itemNumber = (index + 1).ToString(CultureInfo.InvariantCulture);

            builder.AppendLine($"--- SOURCE ITEM {itemNumber} ---");
            builder.AppendLine($"Sent: {FormatIsoDate(sentDate)} ({FormatDate(sentDate)})");
            builder.AppendLine($"From: {item.FromName}");
            builder.AppendLine($"Student: {item.StudentName}");

            if (!string.IsNullOrWhiteSpace(item.NewsletterUrl))
                builder.AppendLine($"Newsletter URL: {item.NewsletterUrl}");

            if (!string.IsNullOrWhiteSpace(item.AiSummary))
                builder.AppendLine($"One-line summary: {item.AiSummary}");

            builder.AppendLine("Content:");
            builder.AppendLine(
                string.IsNullOrWhiteSpace(item.NewsContent)
                    ? "(empty)"
                    : ApplyCharBudget(item.NewsContent));
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Caps one source item at the same budget the digest prompt applied to it, stating plainly how
    /// much was dropped so the critic does not read a mid-sentence stop as the end of the source
    /// and report the draft for omitting what follows.
    /// </summary>
    private static string ApplyCharBudget(string content)
    {
        if (content.Length <= MaxSourceContentChars)
            return content;

        var cut = MaxSourceContentChars;
        if (char.IsHighSurrogate(content[cut - 1]))
            cut--;

        var omitted = (content.Length - cut).ToString(CultureInfo.InvariantCulture);
        var total = content.Length.ToString(CultureInfo.InvariantCulture);

        return content[..cut]
            + Environment.NewLine
            + string.Format(CultureInfo.InvariantCulture, TruncationNoticeFormat, omitted, total);
    }

    private static string LoadDefaultTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "SummaryCritiquePromptTemplate.txt");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Summary critique prompt template not found at '{path}'.", path);

        return File.ReadAllText(path);
    }
}
