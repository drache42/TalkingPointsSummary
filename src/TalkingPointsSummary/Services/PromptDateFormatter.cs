using System.Globalization;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Shared rendering of a stored UTC send timestamp for AI prompts. Both the summary and the
/// categorization prompt builders use this so message dates are presented identically and in the
/// same local timezone the digest treats as "today".
/// </summary>
internal static class PromptDateFormatter
{
    /// <summary>
    /// Renders a UTC send timestamp in the supplied local timezone, e.g.
    /// "Thursday, May 14, 2026 8:30 PM (school local time, UTC-04:00)". Rendering in the reader's
    /// zone keeps relative references ("this Thursday", "tomorrow") anchored to the day the reader
    /// experienced rather than a UTC day that may have already rolled over.
    /// </summary>
    /// <param name="sentAtUtc">Send instant, treated as UTC regardless of its <see cref="DateTimeKind"/>.</param>
    /// <param name="timeZone">Timezone to render the timestamp in.</param>
    public static string FormatSentAt(DateTime sentAtUtc, TimeZoneInfo timeZone)
    {
        var utc = DateTime.SpecifyKind(sentAtUtc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
        var localWithOffset = new DateTimeOffset(local, timeZone.GetUtcOffset(local));
        var friendly = localWithOffset.ToString("dddd, MMMM d, yyyy h:mm tt", CultureInfo.InvariantCulture);
        var offset = localWithOffset.ToString("zzz", CultureInfo.InvariantCulture);
        return $"{friendly} (school local time, UTC{offset})";
    }
}
