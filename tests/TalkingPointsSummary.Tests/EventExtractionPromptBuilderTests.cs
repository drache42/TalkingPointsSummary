using FluentAssertions;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

/// <summary>
/// Verifies prompt construction for event extraction: the date reference calendar, the tracked
/// event list the model replaces and cancels by id, and token substitution.
/// </summary>
public class EventExtractionPromptBuilderTests
{
    // The news item was sent on a Monday. Every calendar expectation below is anchored to it.
    // 14:30 UTC is 09:30 on the same day in Eastern time, so the anchor is Monday either way.
    private static readonly DateTime SentAt = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

    // The school and its families read dates in local time, not UTC.
    private static readonly TimeZoneInfo SchoolTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void FormatDate_RendersWeekdayMonthDayAndYear()
    {
        EventExtractionPromptBuilder.FormatDate(new DateTime(2026, 3, 2))
            .Should().Be("Monday, March 2, 2026");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Constructor_EmptyTemplate_Throws(string template)
    {
        var act = () => new EventExtractionPromptBuilder(template);

        act.Should().Throw<ArgumentException>().WithParameterName("template");
    }

    [Fact]
    public void Build_ReplacesEveryToken()
    {
        var builder = new EventExtractionPromptBuilder(
            """
            Anchor: {{ANCHOR_DATE}}
            School: {{SCHOOL}}
            Student: {{STUDENT_NAME}}
            From: {{FROM_NAME}}
            Content: {{NEWS_CONTENT}}
            Existing:
            {{EXISTING_EVENTS}}
            Calendar:
            {{DATE_REFERENCE}}
            """);

        var prompt = builder.Build(SampleNewsItem(), "Maple Elementary", [], SchoolTimeZone);

        prompt.Should().NotContain("{{");
        prompt.Should().Contain("Anchor: Monday, March 2, 2026");
        prompt.Should().Contain("School: Maple Elementary");
        prompt.Should().Contain("Student: Kid");
        prompt.Should().Contain("From: Ms. Zutz");
        prompt.Should().Contain("Content: Book fair is this Thursday.");
    }

    [Fact]
    public void Build_NoTrackedEvents_RendersAnExplicitNonePlaceholder()
    {
        var builder = new EventExtractionPromptBuilder("[{{EXISTING_EVENTS}}]");

        var prompt = builder.Build(SampleNewsItem(), "Maple Elementary", [], SchoolTimeZone);

        // An empty string would leave the "Refer to these by their numeric id:" header of the
        // shipped template dangling with nothing under it.
        prompt.Should().Be("[None]");
    }

    [Fact]
    public void Build_TrackedEventWithoutATime_SaysSoRatherThanRenderingAnEmptyBracket()
    {
        var builder = new EventExtractionPromptBuilder("{{EXISTING_EVENTS}}");

        var events = new[]
        {
            SampleEvent(11, new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc), "Spring Concert", timeText: null),
            SampleEvent(12, new DateTime(2026, 3, 24, 0, 0, 0, DateTimeKind.Utc), "Field Day", "   "),
            SampleEvent(13, new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc), "Book Fair", "6:30 PM")
        };

        var prompt = builder.Build(SampleNewsItem(), "Maple Elementary", events, SchoolTimeZone);

        prompt.Should().Be(string.Join(Environment.NewLine,
            "- id 11: 2026-03-20 (no time given) Spring Concert",
            "- id 12: 2026-03-24 (no time given) Field Day",
            "- id 13: 2026-03-26 (6:30 PM) Book Fair"));
    }

    [Fact]
    public void Build_DateReferenceCalendar_CoversAWeekBackAndFourMonthsForward()
    {
        var builder = new EventExtractionPromptBuilder("{{DATE_REFERENCE}}");

        var prompt = builder.Build(SampleNewsItem(), "Maple Elementary", [], SchoolTimeZone);

        // A week of history is enough to resolve "last Friday" in a newsletter that arrived late.
        prompt.Should().StartWith("- 2026-02-23 = Monday, February 23, 2026");
        prompt.Should().NotContain("2026-02-22");

        // End-of-year announcements land months out, so the window has to reach that far or the
        // model is left doing calendar arithmetic unaided.
        prompt.Should().Contain("- 2026-03-05 = Thursday, March 5, 2026");
        prompt.Should().EndWith("- 2026-06-30 = Tuesday, June 30, 2026");
        prompt.Should().NotContain("2026-07-01");

        prompt.Split('\n').Should().HaveCount(128, "seven days before the anchor, the anchor, and 120 after");
    }

    [Fact]
    public void Build_AnchorsOnTheSendDateNotItsTimeOfDay()
    {
        var builder = new EventExtractionPromptBuilder("{{ANCHOR_DATE}}|{{DATE_REFERENCE}}");

        var lateEvening = SampleNewsItem();
        lateEvening.SentAt = new DateTime(2026, 3, 2, 23, 59, 0, DateTimeKind.Utc);

        var prompt = builder.Build(lateEvening, "Maple Elementary", [], SchoolTimeZone);

        prompt.Should().StartWith("Monday, March 2, 2026|");
        prompt.Should().Contain("- 2026-03-02 = Monday, March 2, 2026");
    }

    [Fact]
    public void Build_EveningSendThatIsAlreadyTheNextDayInUtc_AnchorsOnTheLocalDate()
    {
        var builder = new EventExtractionPromptBuilder("{{ANCHOR_DATE}}|{{DATE_REFERENCE}}");

        // 19:00 Monday in New York is midnight Tuesday in UTC. School announcements go out in the
        // evening all the time, and anchoring on the UTC date would resolve "tomorrow" in the text
        // to Wednesday instead of Tuesday, storing every relative date one day late.
        var evening = SampleNewsItem();
        evening.SentAt = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);

        var prompt = builder.Build(evening, "Maple Elementary", [], SchoolTimeZone);

        prompt.Should().StartWith("Monday, March 2, 2026|");
        prompt.Should().Contain("- 2026-03-03 = Tuesday, March 3, 2026");
    }

    [Fact]
    public void Build_SameInstantInADifferentTimeZone_AnchorsOnThatZonesDate()
    {
        var builder = new EventExtractionPromptBuilder("{{ANCHOR_DATE}}");

        var evening = SampleNewsItem();
        evening.SentAt = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);

        // The same instant is already Tuesday for a school that keeps UTC.
        builder.Build(evening, "Maple Elementary", [], TimeZoneInfo.Utc)
            .Should().Be("Tuesday, March 3, 2026");
    }

    [Fact]
    public void Build_NullArguments_Throw()
    {
        var builder = new EventExtractionPromptBuilder("{{SCHOOL}}");

        var nullNewsItem = () => builder.Build(null!, "Maple Elementary", [], SchoolTimeZone);
        var nullSchool = () => builder.Build(SampleNewsItem(), null!, [], SchoolTimeZone);
        var nullEvents = () => builder.Build(SampleNewsItem(), "Maple Elementary", null!, SchoolTimeZone);
        var nullTimeZone = () => builder.Build(SampleNewsItem(), "Maple Elementary", [], null!);

        nullNewsItem.Should().Throw<ArgumentNullException>();
        nullSchool.Should().Throw<ArgumentNullException>();
        nullEvents.Should().Throw<ArgumentNullException>();
        nullTimeZone.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DefaultTemplate_AsksForAbsoluteDatesAndOffersTheTrackedEventIds()
    {
        var prompt = new EventExtractionPromptBuilder().Build(
            SampleNewsItem(),
            "Maple Elementary",
            [SampleEvent(11, new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc), "Spring Concert", "6:30 PM")],
            SchoolTimeZone);

        prompt.Should().NotContain("{{");
        prompt.Should().Contain("The news item below was sent on Monday, March 2, 2026.");
        prompt.Should().Contain("\"event_date\": \"YYYY-MM-DD\"");
        prompt.Should().Contain("- id 11: 2026-03-20 (6:30 PM) Spring Concert");
    }

    private static NewsItem SampleNewsItem() => new()
    {
        ParentId = 1,
        SourceMessageId = "source",
        SourceType = SourceType.NewsletterUrl,
        NewsContent = "Book fair is this Thursday.",
        AiSummary = "summary",
        FromName = "Ms. Zutz",
        StudentName = "Kid",
        SentAt = SentAt,
        AnalyzedAt = SentAt,
        CreatedAt = SentAt
    };

    private static TrackedEvent SampleEvent(int id, DateTime eventDate, string title, string? timeText) => new()
    {
        Id = id,
        ParentId = 1,
        School = "Maple Elementary",
        EventDate = eventDate,
        Title = title,
        TimeText = timeText,
        Status = TrackedEventStatus.Active,
        CreatedAt = SentAt
    };
}
