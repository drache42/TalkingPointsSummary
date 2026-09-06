using FluentAssertions;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

/// <summary>
/// Verifies prompt construction for weekly summary generation.
/// </summary>
public class SummaryPromptBuilderTests
{
    /// <summary>
    /// Verifies that child, school-wide, and date-grouped sections include school context.
    /// </summary>
    [Fact]
    public void Build_IncludesSchoolInChildHeadersAndSchoolGroupedDateSections()
    {
        var builder = new SummaryPromptBuilder(
            """
            {{SCHOOL_WIDE_SECTIONS}}

            {{CHILD_SECTIONS}}

            ## Important Upcoming Dates
            {{SCHOOL_DATE_SECTIONS}}
            """);

        var children = new List<Child>
        {
            new() { Name = "StudentOne", School = "Sample Elementary", StartingGrade = 0, StartingYear = 2025, Emoji = "📚" },
            new() { Name = "StudentTwo", School = "Demo Elementary", StartingGrade = 3, StartingYear = 2025, Emoji = "🎓" }
        };

        var prompt = builder.Build(
            new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc),
            TimeZoneInfo.Utc,
            children,
            [],
            []);

        prompt.Should().Contain("# 📚 StudentOne (Kindergarten at Sample Elementary)");
        prompt.Should().Contain("# 🎓 StudentTwo (3rd Grade at Demo Elementary)");
        prompt.Should().Contain("## Sample Elementary School-Wide News");
        prompt.Should().Contain("## Demo Elementary School-Wide News");
        prompt.Should().Contain("### Sample Elementary (StudentOne)");
        prompt.Should().Contain("### Demo Elementary (StudentTwo)");
        prompt.Should().Contain("- **[Date]** – [Event] ([Time if applicable])");
    }

    /// <summary>
    /// Verifies that recent news and previous summaries are expanded into the prompt.
    /// </summary>
    [Fact]
    public void Build_ExpandsRecentNewsAndPreviousSummaries()
    {
        var builder = new SummaryPromptBuilder(
            """
            Today: {{TODAY}}
            Context:
            {{CONTEXT}}
            News:
            {{RECENT_NEWS}}
            Previous:
            {{PREVIOUS_SUMMARIES}}
            """);

        var children = new List<Child>
        {
            new() { Name = "StudentOne", School = "Sample Elementary", StartingGrade = 0, StartingYear = 2025, Emoji = "📚" }
        };

        var newsItems = new List<NewsItem>
        {
            new()
            {
                StudentName = "StudentOne",
                FromName = "Ms. Zutz",
                SourceType = SourceType.NewsletterUrl,
                SentAt = new DateTime(2026, 3, 7, 15, 0, 0, DateTimeKind.Utc),
                NewsContent = "Detailed classroom update"
            }
        };

        var previousSummaries = new List<Summary>
        {
            new() { Content = "Earlier summary" }
        };

        var prompt = builder.Build(
            new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc),
            TimeZoneInfo.Utc,
            children,
            newsItems,
            previousSummaries);

        prompt.Should().Contain("Today: Monday, March 9, 2026");
        prompt.Should().Contain("- StudentOne (Sample Elementary) — Kindergarten");
        prompt.Should().Contain("Type: Newsletter");
        prompt.Should().Contain("Date Sent: Saturday, March 7, 2026 3:00 PM (school local time, UTC+00:00)");
        prompt.Should().Contain("Content: Detailed classroom update");
        prompt.Should().Contain("Earlier summary");
    }

    /// <summary>
    /// Verifies that a news item's "Date Sent" is rendered in the supplied local timezone, not UTC.
    /// A message sent Thursday evening US Eastern crosses midnight in UTC; rendering it as the UTC day
    /// (Friday) would make the model anchor relative references like "tomorrow" one day late.
    /// </summary>
    [Fact]
    public void Build_RecentNews_RendersDateSentInSuppliedTimeZone_NotUtc()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var builder = new SummaryPromptBuilder("{{RECENT_NEWS}}");

        var newsItems = new List<NewsItem>
        {
            new()
            {
                StudentName = "StudentOne",
                FromName = "Ms. Zutz",
                SourceType = SourceType.MessageText,
                // 2026-05-15 00:30 UTC == 2026-05-14 20:30 America/New_York (EDT, UTC-04:00)
                SentAt = new DateTime(2026, 5, 15, 0, 30, 0, DateTimeKind.Utc),
                NewsContent = "Early dismissal tomorrow"
            }
        };

        var prompt = builder.Build(
            new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Unspecified),
            eastern,
            [],
            newsItems,
            []);

        prompt.Should().Contain("Date Sent: Thursday, May 14, 2026 8:30 PM (school local time, UTC-04:00)");
        // Guard against a regression that drops the conversion and renders the UTC day instead.
        prompt.Should().NotContain("Friday, May 15, 2026");
    }

    /// <summary>
    /// Verifies that the previous-summary section renders `None` when no summaries exist.
    /// </summary>
    [Fact]
    public void Build_EmptyPreviousSummaries_ReturnsNone()
    {
        var builder = new SummaryPromptBuilder("Previous: {{PREVIOUS_SUMMARIES}}");

        var prompt = builder.Build(DateTime.UtcNow, TimeZoneInfo.Utc, [], [], []);

        prompt.Should().Be("Previous: None");
    }

    /// <summary>
    /// Verifies that child-specific sections remain empty when no children are provided.
    /// </summary>
    [Fact]
    public void Build_EmptyChildrenList_ProducesNoChildSections()
    {
        var builder = new SummaryPromptBuilder(
            "Context: {{CONTEXT}}\nChildren: {{CHILD_SECTIONS}}\nSchoolWide: {{SCHOOL_WIDE_SECTIONS}}\nDates: {{SCHOOL_DATE_SECTIONS}}");

        var prompt = builder.Build(DateTime.UtcNow, TimeZoneInfo.Utc, [], [], []);

        // With no children, the sections should be empty/trimmed
        prompt.Should().Contain("Context: ");
        prompt.Should().Contain("Children: ");
    }

    /// <summary>
    /// Verifies that all supported template tokens are replaced during prompt generation.
    /// </summary>
    [Fact]
    public void Build_AllNineTokensAreReplaced()
    {
        var template = "{{TODAY}}|{{SUMMARY_TITLE}}|{{WEEK_CALENDAR}}|{{CONTEXT}}|{{RECENT_NEWS}}|{{PREVIOUS_SUMMARIES}}|{{SCHOOL_WIDE_SECTIONS}}|{{CHILD_SECTIONS}}|{{SCHOOL_DATE_SECTIONS}}";
        var builder = new SummaryPromptBuilder(template);

        var children = new List<Child>
        {
            new() { Name = "StudentOne", School = "Sample Elementary", StartingGrade = 0, StartingYear = 2025, Emoji = "📚" }
        };

        var prompt = builder.Build(new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc, children, [], []);

        prompt.Should().NotContain("{{");
        prompt.Should().NotContain("}}");
    }

    [Fact]
    public void Build_WeekCalendar_IsReplacedWithExpectedDateRange()
    {
        var builder = new SummaryPromptBuilder("{{WEEK_CALENDAR}}");
        var now = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);

        var prompt = builder.Build(now, TimeZoneInfo.Utc, [], [], []);

        prompt.Should().NotContain("{{WEEK_CALENDAR}}");
        prompt.Should().Contain("Friday, January 23, 2026");  // -45 days (start, covers the 6-week news window)
        prompt.Should().Contain("Monday, March 2, 2026");     // -7 days
        prompt.Should().Contain("Monday, March 9, 2026");     // today
        prompt.Should().Contain("Monday, March 23, 2026");    // +14 days
        prompt.Should().Contain("Friday, July 3, 2026");      // +116 days (near end)
    }

    /// <summary>
    /// Verifies that {{SUMMARY_TITLE}} is replaced with a heading that contains the formatted date.
    /// </summary>
    [Fact]
    public void Build_SummaryTitle_ContainsFormattedDate()
    {
        var builder = new SummaryPromptBuilder("{{SUMMARY_TITLE}}");
        var now = new DateTime(2026, 5, 24, 8, 0, 0, DateTimeKind.Unspecified); // local Sunday

        var prompt = builder.Build(now, TimeZoneInfo.Utc, [], [], []);

        prompt.Should().NotContain("{{SUMMARY_TITLE}}");
        prompt.Should().Contain("Sunday, May 24, 2026");
    }

    /// <summary>
    /// Verifies that {{TODAY}} and {{SUMMARY_TITLE}} reflect the date of the supplied local datetime,
    /// not a different date. This matters because SummaryGenerator converts UTC to the configured local timezone
    /// before calling Build(), so the prompt must faithfully forward whatever date it receives.
    /// </summary>
    [Theory]
    [InlineData(2026, 5, 24, "Sunday, May 24, 2026")]    // Sunday — typical prod run day
    [InlineData(2026, 5, 25, "Monday, May 25, 2026")]    // Monday
    [InlineData(2026, 9, 1,  "Tuesday, September 1, 2026")] // September boundary (grade year edge)
    public void Build_TodayAndTitle_ReflectSuppliedDate(int year, int month, int day, string expectedDayDate)
    {
        var builder = new SummaryPromptBuilder("{{TODAY}}|{{SUMMARY_TITLE}}");
        var now = new DateTime(year, month, day, 8, 0, 0, DateTimeKind.Unspecified);

        var prompt = builder.Build(now, TimeZoneInfo.Utc, [], [], []);

        prompt.Should().Contain(expectedDayDate);
    }
}