using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Category of problem detected in a generated summary digest.
/// </summary>
public enum SummaryValidationFindingKind
{
    /// <summary>
    /// The output states a day of week that does not match the calendar date it is attached to,
    /// for example "Friday, May 15, 2026" when that date is a Thursday.
    /// </summary>
    WeekdayDateMismatch,

    /// <summary>
    /// A date listed under the "Important Upcoming Dates" heading is earlier than the supplied
    /// current date, so it is not actually upcoming.
    /// </summary>
    PastUpcomingDate,

    /// <summary>
    /// The output uses a relative time reference such as "today", "tomorrow", or "next Friday".
    /// The digest template requires explicit weekday plus date instead.
    /// </summary>
    RelativeDateTerm,

    /// <summary>
    /// Two dates inside the same school subsection of the upcoming dates section are not in
    /// chronological order.
    /// </summary>
    OutOfOrderDates,

    /// <summary>
    /// A date is further in the future than a school digest can plausibly reference.
    /// </summary>
    ImplausiblyDistantDate,

    /// <summary>
    /// A date is written without a four-digit year, so it cannot be resolved. The validator
    /// reports this rather than guessing, because a guess is wrong across a year boundary.
    /// </summary>
    MissingYear,

    /// <summary>
    /// A date names a day that does not exist in that month, for example "February 30, 2026".
    /// </summary>
    InvalidDate,

    /// <summary>
    /// An event from the authoritative "Important Upcoming Dates" block the model was handed does
    /// not appear in the digest it produced. The block is a recorded fact rendered in code, so a
    /// line missing from the output is the model having dropped or rewritten it.
    /// </summary>
    MissingUpcomingDate
}

/// <summary>
/// A single problem found in a generated summary digest, with enough detail to feed a revision prompt.
/// </summary>
public sealed record SummaryValidationFinding
{
    /// <summary>
    /// Initializes a new finding.
    /// </summary>
    /// <param name="kind">Category of the problem.</param>
    /// <param name="lineNumber">One-based line number in the digest where the problem appears.</param>
    /// <param name="excerpt">Trimmed text of the offending line.</param>
    /// <param name="message">Human-readable explanation of the problem and how to fix it.</param>
    public SummaryValidationFinding(
        SummaryValidationFindingKind kind,
        int lineNumber,
        string excerpt,
        string message)
    {
        Kind = kind;
        LineNumber = lineNumber;
        Excerpt = excerpt;
        Message = message;
    }

    /// <summary>
    /// Category of the problem.
    /// </summary>
    public SummaryValidationFindingKind Kind { get; init; }

    /// <summary>
    /// One-based line number in the digest where the problem appears.
    /// </summary>
    public int LineNumber { get; init; }

    /// <summary>
    /// Trimmed text of the offending line.
    /// </summary>
    public string Excerpt { get; init; }

    /// <summary>
    /// Human-readable explanation of the problem and how to fix it.
    /// </summary>
    public string Message { get; init; }

    /// <summary>
    /// Renders the finding as a single line suitable for inclusion in a revision prompt.
    /// </summary>
    public string Describe()
    {
        var line = LineNumber.ToString(CultureInfo.InvariantCulture);
        return $"Line {line} ({Kind}): {Message} Text: \"{Excerpt}\"";
    }
}

/// <summary>
/// Validates the generated markdown digest on its own terms, without access to the source messages.
/// It checks the internal date consistency rules the summary prompt template requires: explicit
/// weekday and date agreement, future-only upcoming dates, no relative time references, chronological
/// ordering inside each school subsection, and plausible date ranges.
/// </summary>
/// <remarks>
/// The current date is always supplied by the caller so results are deterministic and testable.
/// "this year" and "next year" are deliberately not treated as relative terms, because in school
/// communications they normally refer to a school year rather than to a shifting point in time.
/// </remarks>
public sealed partial class SummaryOutputValidator
{
    /// <summary>
    /// Number of months past the supplied current date after which a referenced date is reported
    /// as implausibly distant.
    /// </summary>
    public const int MaxPlausibleMonthsAhead = 18;

    private const int MaxExcerptLength = 160;
    private const string UpcomingDatesHeadingText = "important upcoming dates";
    private const string LongDateFormat = "MMMM d, yyyy";

    /// <summary>
    /// A leap year, used only to ask how long a month can be when no year is stated.
    /// </summary>
    private const int LeapReferenceYear = 2024;

    /// <summary>
    /// Years relative to the supplied current date that a date written without a year could mean.
    /// A school digest never refers further out than that.
    /// </summary>
    private static readonly int[] CandidateYearOffsets = [-1, 0, 1];

    private static readonly Dictionary<string, int> MonthNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["January"] = 1,
        ["Jan"] = 1,
        ["February"] = 2,
        ["Feb"] = 2,
        ["March"] = 3,
        ["Mar"] = 3,
        ["April"] = 4,
        ["Apr"] = 4,
        ["May"] = 5,
        ["June"] = 6,
        ["Jun"] = 6,
        ["July"] = 7,
        ["Jul"] = 7,
        ["August"] = 8,
        ["Aug"] = 8,
        ["September"] = 9,
        ["Sept"] = 9,
        ["Sep"] = 9,
        ["October"] = 10,
        ["Oct"] = 10,
        ["November"] = 11,
        ["Nov"] = 11,
        ["December"] = 12,
        ["Dec"] = 12
    };

    private static readonly Dictionary<string, DayOfWeek> WeekdayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sunday"] = DayOfWeek.Sunday,
        ["Sun"] = DayOfWeek.Sunday,
        ["Monday"] = DayOfWeek.Monday,
        ["Mon"] = DayOfWeek.Monday,
        ["Tuesday"] = DayOfWeek.Tuesday,
        ["Tues"] = DayOfWeek.Tuesday,
        ["Tue"] = DayOfWeek.Tuesday,
        ["Wednesday"] = DayOfWeek.Wednesday,
        ["Weds"] = DayOfWeek.Wednesday,
        ["Wed"] = DayOfWeek.Wednesday,
        ["Thursday"] = DayOfWeek.Thursday,
        ["Thurs"] = DayOfWeek.Thursday,
        ["Thu"] = DayOfWeek.Thursday,
        ["Friday"] = DayOfWeek.Friday,
        ["Fri"] = DayOfWeek.Friday,
        ["Saturday"] = DayOfWeek.Saturday,
        ["Sat"] = DayOfWeek.Saturday
    };

    /// <summary>
    /// Validates a generated digest against the output rules the prompt template imposes.
    /// </summary>
    /// <param name="markdown">The generated markdown digest.</param>
    /// <param name="today">
    /// The date the digest is generated and sent. Supplied by the caller rather than read from the
    /// clock so validation is deterministic. Any time component is ignored.
    /// </param>
    /// <param name="expectedUpcomingDates">
    /// The authoritative "Important Upcoming Dates" block the model was handed to copy, exactly as
    /// it was rendered. When supplied, every event line in it must be reproduced in the digest.
    /// Null skips the check, which is what a caller with no rendered block does.
    /// </param>
    /// <returns>
    /// Findings in document order, with any missing upcoming dates appended last. An empty list
    /// means no rule violations were detected.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="markdown"/> is null.</exception>
    public IReadOnlyList<SummaryValidationFinding> Validate(
        string markdown,
        DateTime today,
        string? expectedUpcomingDates = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var findings = new List<SummaryValidationFinding>();
        if (markdown.Length == 0)
        {
            AddMissingUpcomingDates(findings, markdown, expectedUpcomingDates);
            return findings;
        }

        var todayDate = today.Date;
        var todayText = todayDate.ToString(LongDateFormat, CultureInfo.InvariantCulture);
        var plausibleLimit = ComputePlausibleLimit(todayDate);

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        var inUpcomingSection = false;
        var upcomingHeadingLevel = 0;
        DateTime? latestDateInSubsection = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var rawLine = lines[index];

            var headingMatch = HeadingPattern().Match(rawLine);
            var isHeading = headingMatch.Success;
            if (isHeading)
            {
                var level = headingMatch.Groups["hashes"].Value.Length;
                var title = headingMatch.Groups["title"].Value;

                if (inUpcomingSection && level <= upcomingHeadingLevel)
                {
                    inUpcomingSection = false;
                    latestDateInSubsection = null;
                }

                if (title.Contains(UpcomingDatesHeadingText, StringComparison.OrdinalIgnoreCase))
                {
                    inUpcomingSection = true;
                    upcomingHeadingLevel = level;
                    latestDateInSubsection = null;
                }
                else if (inUpcomingSection)
                {
                    // A deeper heading starts a new school subsection; ordering restarts there.
                    latestDateInSubsection = null;
                }
            }

            if (rawLine.Trim().Length == 0)
                continue;

            var scanLine = MaskLinks(rawLine);
            var excerpt = BuildExcerpt(rawLine);

            foreach (Match relative in RelativeTermPattern().Matches(scanLine))
            {
                var term = CollapseWhitespace(relative.Value);
                findings.Add(new SummaryValidationFinding(
                    SummaryValidationFindingKind.RelativeDateTerm,
                    lineNumber,
                    excerpt,
                    $"Output uses the relative time reference \"{term}\". Replace it with an explicit weekday and full date, for example \"Monday, {todayText}\"."));
            }

            var chronologySlotUsed = false;

            foreach (Match dateMatch in DatePattern().Matches(scanLine))
            {
                var monthToken = dateMatch.Groups["month"].Value;

                // Dates in the digest are always capitalized. Requiring a capital keeps ordinary
                // words such as "may" or "march" from being read as months.
                if (monthToken.Length == 0 || !char.IsUpper(monthToken[0]))
                    continue;

                if (!MonthNumbers.TryGetValue(monthToken, out var month))
                    continue;

                // A year written once at the far end of a range belongs to the first date too, and
                // the tail is matched without being consumed so the later dates in the range are
                // still validated in their own right. The one case it does not carry is a range
                // that wraps into a lower month: "December 20 through January 5, 2027" states 2027
                // for January only, so December is left reported as missing its year rather than
                // being read as a year later than it means.
                var tailGroup = dateMatch.Groups["tail"];
                var tailMonthGroup = dateMatch.Groups["tailMonth"];
                var yearGroup = dateMatch.Groups["year"];
                var yearCarriesToThisDate = yearGroup.Success
                    && (!tailMonthGroup.Success
                        || !MonthNumbers.TryGetValue(tailMonthGroup.Value, out var tailMonth)
                        || tailMonth >= month);

                var dateText = CollapseWhitespace(
                    yearCarriesToThisDate && tailGroup.Success
                        ? dateMatch.Value + tailGroup.Value
                        : dateMatch.Value);

                var dayGroup = dateMatch.Groups["day"].Value;
                if (!int.TryParse(dayGroup, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
                    continue;

                var weekdayGroup = dateMatch.Groups["weekday"];
                DayOfWeek? statedWeekday =
                    weekdayGroup.Success && WeekdayNames.TryGetValue(weekdayGroup.Value, out var parsedWeekday)
                        ? parsedWeekday
                        : null;

                DateTime date;

                if (yearCarriesToThisDate)
                {
                    if (!int.TryParse(yearGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                        || year < 1
                        || year > 9999)
                    {
                        continue;
                    }

                    if (day < 1 || day > DateTime.DaysInMonth(year, month))
                    {
                        findings.Add(new SummaryValidationFinding(
                            SummaryValidationFindingKind.InvalidDate,
                            lineNumber,
                            excerpt,
                            $"\"{dateText}\" is not a valid calendar date."));
                        continue;
                    }

                    date = new DateTime(year, month, day);
                    var dateAsText = date.ToString(LongDateFormat, CultureInfo.InvariantCulture);

                    if (statedWeekday.HasValue && statedWeekday.Value != date.DayOfWeek)
                    {
                        findings.Add(new SummaryValidationFinding(
                            SummaryValidationFindingKind.WeekdayDateMismatch,
                            lineNumber,
                            excerpt,
                            $"Output says \"{dateText}\", but {dateAsText} falls on a {date.DayOfWeek}, not a {statedWeekday.Value}."));
                    }

                    if (date > plausibleLimit)
                    {
                        var months = MaxPlausibleMonthsAhead.ToString(CultureInfo.InvariantCulture);
                        findings.Add(new SummaryValidationFinding(
                            SummaryValidationFindingKind.ImplausiblyDistantDate,
                            lineNumber,
                            excerpt,
                            $"\"{dateText}\" is more than {months} months after {todayText} and is implausible for a school digest. Check the year."));
                    }

                    if (!inUpcomingSection || isHeading)
                        continue;

                    if (date < todayDate)
                    {
                        findings.Add(new SummaryValidationFinding(
                            SummaryValidationFindingKind.PastUpcomingDate,
                            lineNumber,
                            excerpt,
                            $"\"{dateText}\" is before {todayText} and must not be listed under Important Upcoming Dates."));
                    }
                }
                else
                {
                    findings.Add(new SummaryValidationFinding(
                        SummaryValidationFindingKind.MissingYear,
                        lineNumber,
                        excerpt,
                        $"The date \"{dateText}\" has no four-digit year, so it cannot be resolved. Every date in the digest must include the year."));

                    // The missing year is reported rather than guessed, but the checks that do not
                    // depend on knowing the year still run, so one omission cannot mask a real error
                    // on the same date.
                    if (day < 1 || day > DaysInLongestMonth(month))
                    {
                        findings.Add(new SummaryValidationFinding(
                            SummaryValidationFindingKind.InvalidDate,
                            lineNumber,
                            excerpt,
                            $"\"{dateText}\" is not a valid calendar date in any year."));
                        continue;
                    }

                    var candidates = NearbyCandidateDates(month, day, todayDate);

                    // Reported only when the stated weekday is wrong for every year the digest
                    // could plausibly mean, so a missing year never produces a false accusation.
                    if (statedWeekday.HasValue
                        && candidates.Count > 0
                        && candidates.TrueForAll(candidate => candidate.DayOfWeek != statedWeekday.Value))
                    {
                        var monthName = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);
                        var dayText = day.ToString(CultureInfo.InvariantCulture);
                        findings.Add(new SummaryValidationFinding(
                            SummaryValidationFindingKind.WeekdayDateMismatch,
                            lineNumber,
                            excerpt,
                            $"Output says \"{dateText}\", but {monthName} {dayText} does not fall on a {statedWeekday.Value} in any year near {todayText}."));
                    }

                    if (!inUpcomingSection || isHeading || candidates.Count == 0)
                        continue;

                    // Under Important Upcoming Dates a yearless date reads as its next occurrence,
                    // so it is never reported as past, but it still takes part in the ordering check
                    // under that reading.
                    date = NextOccurrence(candidates, todayDate);
                }

                if (chronologySlotUsed)
                    continue;

                chronologySlotUsed = true;
                if (latestDateInSubsection.HasValue && date < latestDateInSubsection.Value)
                {
                    var previous = latestDateInSubsection.Value.ToString(LongDateFormat, CultureInfo.InvariantCulture);
                    findings.Add(new SummaryValidationFinding(
                        SummaryValidationFindingKind.OutOfOrderDates,
                        lineNumber,
                        excerpt,
                        $"\"{dateText}\" is listed after {previous} but is chronologically earlier. Sort dates within each school section."));
                }

                if (!latestDateInSubsection.HasValue || date > latestDateInSubsection.Value)
                    latestDateInSubsection = date;
            }
        }

        AddMissingUpcomingDates(findings, markdown, expectedUpcomingDates);

        return findings;
    }

    /// <summary>
    /// Reports every event line from the authoritative upcoming-dates block that the digest failed
    /// to reproduce.
    /// </summary>
    /// <remarks>
    /// The block is rendered in code from tracked events precisely so the model does not have to
    /// rebuild it, and every other check here reads the digest on its own terms: a digest that
    /// silently drops the whole section, or two of its three lines, is internally consistent and
    /// passes all of them. This is the only check that compares the output against what the model
    /// was told to copy, so it is also what makes a revision that loses content score worse than
    /// the draft rather than the same.
    /// </remarks>
    private static void AddMissingUpcomingDates(
        List<SummaryValidationFinding> findings,
        string markdown,
        string? expectedUpcomingDates)
    {
        if (string.IsNullOrWhiteSpace(expectedUpcomingDates))
            return;

        var haystack = CollapseWhitespace(markdown);

        foreach (Match line in UpcomingDateLinePattern().Matches(expectedUpcomingDates))
        {
            var date = CollapseWhitespace(line.Groups["date"].Value);

            // The trailing "(6:30 PM)" is the announcement's own wording, and a model that keeps
            // the event but writes the time differently has not lost anything. The date and the
            // event name are the facts that have to survive.
            var title = CollapseWhitespace(StripTrailingParenthetical(line.Groups["title"].Value));

            if (date.Length == 0 || title.Length == 0)
                continue;

            if (haystack.Contains(date, StringComparison.OrdinalIgnoreCase)
                && haystack.Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            findings.Add(new SummaryValidationFinding(
                SummaryValidationFindingKind.MissingUpcomingDate,
                0,
                $"- **{date}** - {title}",
                $"The digest does not carry the tracked event \"{title}\" on {date}. Reproduce the "
                + "Important Upcoming Dates section exactly as supplied, with every line."));
        }
    }

    /// <summary>
    /// Removes a trailing parenthetical, which in a rendered upcoming-date line is the free-text
    /// time of day.
    /// </summary>
    private static string StripTrailingParenthetical(string title)
    {
        var trimmed = title.TrimEnd();
        if (!trimmed.EndsWith(')'))
            return trimmed;

        var open = trimmed.LastIndexOf('(');
        return open <= 0 ? trimmed : trimmed[..open].TrimEnd();
    }

    /// <summary>
    /// Renders findings as a numbered list for inclusion in a revision prompt. Lines are separated
    /// by a line feed regardless of platform so prompt text is identical everywhere.
    /// </summary>
    /// <param name="findings">Findings to render.</param>
    /// <returns>An empty string when there are no findings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="findings"/> is null.</exception>
    public static string FormatForRevisionPrompt(IEnumerable<SummaryValidationFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var builder = new StringBuilder();
        var position = 0;
        foreach (var finding in findings)
        {
            position++;
            var number = position.ToString(CultureInfo.InvariantCulture);
            builder.Append(number).Append(". ").Append(finding.Describe()).Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Longest a month can be in any year, so "February 29" is accepted without a year while
    /// "February 30" and "April 31" are rejected.
    /// </summary>
    private static int DaysInLongestMonth(int month) => DateTime.DaysInMonth(LeapReferenceYear, month);

    /// <summary>
    /// The dates a yearless "Month day" could mean, limited to the year before and after the
    /// supplied current date. An empty list means the day exists in no nearby year, as
    /// "February 29" does when no neighbouring year is a leap year.
    /// </summary>
    private static List<DateTime> NearbyCandidateDates(int month, int day, DateTime todayDate)
    {
        var candidates = new List<DateTime>(CandidateYearOffsets.Length);
        foreach (var offset in CandidateYearOffsets)
        {
            var year = todayDate.Year + offset;
            if (year < 1 || year > 9999)
                continue;

            if (day >= 1 && day <= DateTime.DaysInMonth(year, month))
                candidates.Add(new DateTime(year, month, day));
        }

        return candidates;
    }

    /// <summary>
    /// The first candidate on or after the current date, falling back to the last candidate when
    /// every one of them is already past.
    /// </summary>
    private static DateTime NextOccurrence(List<DateTime> candidates, DateTime todayDate)
    {
        foreach (var candidate in candidates)
        {
            if (candidate >= todayDate)
                return candidate;
        }

        return candidates[^1];
    }

    private static DateTime ComputePlausibleLimit(DateTime todayDate)
    {
        var latestSafeStart = DateTime.MaxValue.Date.AddMonths(-MaxPlausibleMonthsAhead);
        return todayDate > latestSafeStart
            ? DateTime.MaxValue.Date
            : todayDate.AddMonths(MaxPlausibleMonthsAhead);
    }

    private static string BuildExcerpt(string line)
    {
        var trimmed = CollapseWhitespace(line);
        return trimmed.Length <= MaxExcerptLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, MaxExcerptLength), "...");
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                lastWasSpace = true;
                continue;
            }

            if (lastWasSpace && builder.Length > 0)
                builder.Append(' ');

            lastWasSpace = false;
            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Blanks out URLs and markdown link targets so a slug such as "/signup-today" is not read as
    /// prose. Replacement preserves length, so line content elsewhere keeps its position.
    /// </summary>
    private static string MaskLinks(string line)
    {
        if (line.Length == 0)
            return line;

        var bareUrls = BareUrlPattern().Matches(line);
        var linkTargets = LinkTargetPattern().Matches(line);
        if (bareUrls.Count == 0 && linkTargets.Count == 0)
            return line;

        var builder = new StringBuilder(line);
        foreach (Match match in bareUrls)
            Blank(builder, match);
        foreach (Match match in linkTargets)
            Blank(builder, match);

        return builder.ToString();
    }

    private static void Blank(StringBuilder builder, Match match)
    {
        for (var index = match.Index; index < match.Index + match.Length; index++)
            builder[index] = ' ';
    }

    [GeneratedRegex(@"^\s{0,3}(?<hashes>#{1,6})\s+(?<title>.*)$")]
    private static partial Regex HeadingPattern();

    /// <summary>
    /// Weekday names and abbreviations the digest may write, as a regex alternation.
    /// </summary>
    private const string WeekdayAlternation =
        "Sunday|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sun|Mon|Tues|Tue|Weds|Wed|Thurs|Thu|Fri|Sat";

    /// <summary>
    /// Month names and abbreviations the digest may write, as a regex alternation.
    /// </summary>
    private const string MonthAlternation =
        "January|February|March|April|May|June|July|August|September|October|November|December" +
        "|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sept|Sep|Oct|Nov|Dec";

    /// <summary>
    /// One further date in a range or pair, written as a bare day ("15-16"), a repeated month
    /// ("May 30 - June 2") or a full weekday and month ("Thursday, October 30 and Friday,
    /// October 31"). Nothing here captures into the primary weekday or month groups.
    /// </summary>
    private const string RangeContinuation =
        @"(?:\s*(?:[-/&,\u2013\u2014]|to|and|or|through)\s*" +
        @"(?:\b(?:" + WeekdayAlternation + @")\b\.?,?\s+)?" +
        @"(?:\b(?<tailMonth>" + MonthAlternation + @")\b\.?\s+)?" +
        @"\d{1,2}(?!\d)(?:st|nd|rd|th)?)+";

    /// <remarks>
    /// The year is optional, and it is allowed to sit at the end of a range rather than directly
    /// after the first day, so "May 15-16, 2026", "May 11 - 15, 2026", "May 15 and 16, 2026",
    /// "May 30 - June 2, 2026" and "Thursday, October 30 and Friday, October 31, 2025" all resolve
    /// the year onto the first date instead of being reported as missing it. That trailing year is
    /// matched through a lookahead rather than consumed, so every later date in the range is
    /// matched again on its own and validated with its own weekday and year.
    /// </remarks>
    [GeneratedRegex(
        @"(?:\b(?<weekday>" + WeekdayAlternation + @")\b\.?,?\s+)?" +
        @"\b(?<month>" + MonthAlternation + @")\b\.?\s+" +
        @"(?<day>\d{1,2})(?!\d)(?:st|nd|rd|th)?" +
        @"(?:\s*,?\s+(?<year>\d{4})\b" +
        @"|(?=(?<tail>" + RangeContinuation + @"\s*,?\s+(?<year>\d{4})\b)))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(
        @"\b(?:today|tomorrow|yesterday|tonight" +
        @"|(?:this|next|last|coming)\s+(?:week|weekend|month|morning|afternoon|evening|night" +
        @"|Sunday|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sun|Mon|Tues|Tue|Weds|Wed|Thurs|Thu|Fri|Sat))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeTermPattern();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareUrlPattern();

    [GeneratedRegex(@"\]\([^)]*\)")]
    private static partial Regex LinkTargetPattern();

    /// <summary>
    /// One event line of a rendered upcoming-dates block, as
    /// <see cref="SummaryPromptBuilder.BuildUpcomingDates"/> writes it.
    /// </summary>
    [GeneratedRegex(@"^\s*-\s*\*\*(?<date>[^*]+)\*\*\s*-\s*(?<title>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex UpcomingDateLinePattern();
}
