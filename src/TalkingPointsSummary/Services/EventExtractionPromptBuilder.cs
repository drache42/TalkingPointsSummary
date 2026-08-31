using System.Globalization;
using System.Text;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Builds the AI prompt used to extract dated school events from a single news item.
/// </summary>
public sealed class EventExtractionPromptBuilder
{
    private const string AnchorDateToken = "{{ANCHOR_DATE}}";
    private const string DateReferenceToken = "{{DATE_REFERENCE}}";
    private const string SchoolToken = "{{SCHOOL}}";
    private const string StudentNameToken = "{{STUDENT_NAME}}";
    private const string FromNameToken = "{{FROM_NAME}}";
    private const string NewsContentToken = "{{NEWS_CONTENT}}";
    private const string ExistingEventsToken = "{{EXISTING_EVENTS}}";

    private const int CalendarDaysBefore = 7;
    private const int CalendarDaysAfter = 120;

    private static readonly Lazy<string> DefaultTemplate = new(LoadDefaultTemplate);

    private readonly string _template;

    /// <summary>
    /// Initializes a prompt builder with the default event extraction template.
    /// </summary>
    public EventExtractionPromptBuilder()
        : this(DefaultTemplate.Value)
    {
    }

    /// <summary>
    /// Initializes a prompt builder with a custom template.
    /// </summary>
    /// <param name="template">Template text containing the supported tokens.</param>
    public EventExtractionPromptBuilder(string template)
    {
        _template = string.IsNullOrWhiteSpace(template)
            ? throw new ArgumentException("Prompt template cannot be empty.", nameof(template))
            : template;
    }

    /// <summary>
    /// Builds an event extraction prompt for a single news item. Relative date references are
    /// anchored to the news item's <see cref="NewsItem.SentAt"/> value, never to the current date.
    /// </summary>
    /// <remarks>
    /// <see cref="NewsItem.SentAt"/> is UTC, but "tomorrow" in a newsletter means the day after the
    /// school's local date. A message sent at 19:00 in America/Los_Angeles is already the next day
    /// in UTC, so anchoring on the UTC date would shift every relative reference in it by one day.
    /// The anchor is therefore the local calendar date in <paramref name="timeZone"/>.
    /// </remarks>
    /// <param name="newsItem">News item whose content is scanned for dated events.</param>
    /// <param name="school">School the news item belongs to.</param>
    /// <param name="activeEvents">Events already tracked as active for that parent and school.</param>
    /// <param name="timeZone">Timezone the school and its families read dates in.</param>
    public string Build(
        NewsItem newsItem,
        string school,
        IReadOnlyList<TrackedEvent> activeEvents,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(newsItem);
        ArgumentNullException.ThrowIfNull(school);
        ArgumentNullException.ThrowIfNull(activeEvents);
        ArgumentNullException.ThrowIfNull(timeZone);

        var sentUtc = DateTime.SpecifyKind(newsItem.SentAt, DateTimeKind.Utc);
        var anchor = TimeZoneInfo.ConvertTimeFromUtc(sentUtc, timeZone).Date;

        var prompt = _template;
        prompt = prompt.Replace(AnchorDateToken, FormatDate(anchor), StringComparison.Ordinal);
        prompt = prompt.Replace(DateReferenceToken, BuildDateReferenceCalendar(anchor), StringComparison.Ordinal);
        prompt = prompt.Replace(SchoolToken, school, StringComparison.Ordinal);
        prompt = prompt.Replace(StudentNameToken, newsItem.StudentName, StringComparison.Ordinal);
        prompt = prompt.Replace(FromNameToken, newsItem.FromName, StringComparison.Ordinal);
        prompt = prompt.Replace(NewsContentToken, newsItem.NewsContent, StringComparison.Ordinal);
        prompt = prompt.Replace(ExistingEventsToken, BuildExistingEvents(activeEvents), StringComparison.Ordinal);
        return prompt;
    }

    /// <summary>
    /// Formats a date the way the prompt and its reference calendar render dates.
    /// </summary>
    /// <param name="date">Date to format.</param>
    public static string FormatDate(DateTime date)
        => date.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);

    private static string BuildDateReferenceCalendar(DateTime anchor)
    {
        var builder = new StringBuilder();
        for (var offset = -CalendarDaysBefore; offset <= CalendarDaysAfter; offset++)
        {
            var date = anchor.AddDays(offset);
            builder.AppendLine(
                $"- {date:yyyy-MM-dd} = {FormatDate(date)}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildExistingEvents(IReadOnlyList<TrackedEvent> activeEvents)
    {
        if (activeEvents.Count == 0)
            return "None";

        var builder = new StringBuilder();
        foreach (var trackedEvent in activeEvents)
        {
            var time = string.IsNullOrWhiteSpace(trackedEvent.TimeText) ? "no time given" : trackedEvent.TimeText;
            builder.AppendLine(
                $"- id {trackedEvent.Id}: {trackedEvent.EventDate:yyyy-MM-dd} ({time}) {trackedEvent.Title}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string LoadDefaultTemplate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "EventExtractionPromptTemplate.txt");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Event extraction prompt template not found at '{path}'.", path);

        return File.ReadAllText(path);
    }
}
