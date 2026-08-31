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
    /// A Monday, so the weekdays asserted below are checkable by hand: March 12 is a Thursday,
    /// March 18 a Wednesday, March 20 a Friday.
    /// </summary>
    private static readonly DateTime Now = new(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);

    private static List<Child> TwoChildren() =>
    [
        new() { Name = "StudentOne", School = "Sample Elementary", StartingGrade = 0, StartingYear = 2025, Emoji = "\U0001F4DA" },
        new() { Name = "StudentTwo", School = "Demo Elementary", StartingGrade = 3, StartingYear = 2025, Emoji = "\U0001F393" }
    ];

    private static NewsItem MakeNewsItem(string content) => new()
    {
        StudentName = "StudentOne",
        FromName = "Ms. Zutz",
        SourceType = SourceType.NewsletterUrl,
        SentAt = new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc),
        NewsContent = content
    };

    /// <summary>
    /// Verifies that child and school-wide sections carry the school name.
    /// </summary>
    [Fact]
    public void Build_IncludesSchoolInChildHeadersAndSchoolWideSections()
    {
        var builder = new SummaryPromptBuilder(
            """
            {{SCHOOL_WIDE_SECTIONS}}

            {{CHILD_SECTIONS}}
            """);

        var prompt = builder.Build(Now, TwoChildren(), [], [], []);

        prompt.Should().Contain("# \U0001F4DA StudentOne (Kindergarten at Sample Elementary)");
        prompt.Should().Contain("# \U0001F393 StudentTwo (3rd Grade at Demo Elementary)");
        prompt.Should().Contain("## Sample Elementary School-Wide News");
        prompt.Should().Contain("## Demo Elementary School-Wide News");
    }

    /// <summary>
    /// The upcoming dates section is rendered from recorded events rather than asked of the model,
    /// so it must come out grouped by school and chronological within each school.
    /// </summary>
    [Fact]
    public void Build_UpcomingDates_AreGroupedBySchoolAndSortedByDate()
    {
        var builder = new SummaryPromptBuilder("{{UPCOMING_DATES}}");

        var events = new List<TrackedEvent>
        {
            new() { School = "Sample Elementary", EventDate = new DateTime(2026, 3, 20), Title = "Book Fair" },
            new() { School = "Demo Elementary", EventDate = new DateTime(2026, 3, 18), Title = "Picture Day", TimeText = "9:00 AM" },
            new() { School = "Sample Elementary", EventDate = new DateTime(2026, 3, 12), Title = "Field Day", TimeText = "9:00 AM" }
        };

        var prompt = builder.Build(Now, TwoChildren(), [], [], events);

        prompt.Should().Contain("### Sample Elementary (StudentOne)");
        prompt.Should().Contain("### Demo Elementary (StudentTwo)");
        prompt.Should().Contain("- **Thursday, March 12, 2026** - Field Day (9:00 AM)");
        prompt.Should().Contain("- **Friday, March 20, 2026** - Book Fair");
        prompt.Should().Contain("- **Wednesday, March 18, 2026** - Picture Day (9:00 AM)");

        prompt.IndexOf("Field Day", StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("Book Fair", StringComparison.Ordinal));

        prompt.IndexOf("Sample Elementary (StudentOne)", StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("Demo Elementary (StudentTwo)", StringComparison.Ordinal));
    }

    /// <summary>
    /// An event with no announced time must not gain an empty parenthesis.
    /// </summary>
    [Fact]
    public void Build_UpcomingDates_EventWithoutATime_HasNoTrailingParenthesis()
    {
        var builder = new SummaryPromptBuilder("{{UPCOMING_DATES}}");

        var events = new List<TrackedEvent>
        {
            new() { School = "Sample Elementary", EventDate = new DateTime(2026, 3, 20), Title = "Book Fair", TimeText = "  " }
        };

        var prompt = builder.Build(Now, TwoChildren(), [], [], events);

        prompt.Should().Contain("- **Friday, March 20, 2026** - Book Fair");
        prompt.Should().NotContain("Book Fair (");
    }

    /// <summary>
    /// With nothing on record the model is told to drop the section, not to invent one.
    /// </summary>
    [Fact]
    public void Build_UpcomingDates_NoEvents_TellsTheModelToOmitTheSection()
    {
        var builder = new SummaryPromptBuilder("{{UPCOMING_DATES}}");

        var prompt = builder.Build(Now, TwoChildren(), [], [], []);

        prompt.Should().Be(SummaryPromptBuilder.NoUpcomingDatesText);
    }

    /// <summary>
    /// An event can name a school no current child attends, for example after a move. It still
    /// gets its own section, just without a child-name suffix it cannot fill in.
    /// </summary>
    [Fact]
    public void Build_UpcomingDates_SchoolWithNoMatchingChild_RendersWithoutNames()
    {
        var builder = new SummaryPromptBuilder("{{UPCOMING_DATES}}");

        var events = new List<TrackedEvent>
        {
            new() { School = "Unlisted Academy", EventDate = new DateTime(2026, 3, 20), Title = "Open House" }
        };

        var prompt = builder.Build(Now, TwoChildren(), [], [], events);

        prompt.Should().Contain("### Unlisted Academy");
        prompt.Should().NotContain("### Unlisted Academy (");
    }

    /// <summary>
    /// Verifies that today, family context, and news items are expanded into the user message.
    /// </summary>
    [Fact]
    public void Build_ExpandsTodayContextAndRecentNews()
    {
        var builder = new SummaryPromptBuilder(
            """
            Today: {{TODAY}}
            Context:
            {{CONTEXT}}
            News:
            {{RECENT_NEWS}}
            """);

        var prompt = builder.Build(Now, TwoChildren(), [MakeNewsItem("Detailed classroom update")], [], []);

        prompt.Should().Contain("Today: Monday, March 9, 2026");
        prompt.Should().Contain("- StudentOne (Sample Elementary) - Kindergarten");
        prompt.Should().Contain("Type: Newsletter");
        prompt.Should().Contain("Content: Detailed classroom update");
    }

    /// <summary>
    /// Scraped newsletter text is unbounded, and eligibility no longer expires with a date window,
    /// so one oversized item would otherwise sit in every prompt until it is reported.
    /// </summary>
    [Fact]
    public void Build_NewsContentOverBudget_IsCutAndSaysHowMuchWasDropped()
    {
        var builder = new SummaryPromptBuilder("{{RECENT_NEWS}}", new GradeCalculator(), 100);

        var prompt = builder.Build(Now, TwoChildren(), [MakeNewsItem(new string('x', 400))], [], []);

        prompt.Should().Contain("[... truncated: 300 of 400 characters omitted from this news item ...]");
        prompt.Should().NotContain(new string('x', 101));
    }

    /// <summary>
    /// Content inside the budget must survive untouched, marker and all.
    /// </summary>
    [Fact]
    public void Build_NewsContentWithinBudget_IsLeftAlone()
    {
        var builder = new SummaryPromptBuilder("{{RECENT_NEWS}}", new GradeCalculator(), 100);

        var content = new string('y', 100);
        var prompt = builder.Build(Now, TwoChildren(), [MakeNewsItem(content)], [], []);

        prompt.Should().Contain("Content: " + content);
        prompt.Should().NotContain("truncated");
    }

    /// <summary>
    /// A budget too small to carry any real content is a configuration mistake, not a silent
    /// setting that empties every news item.
    /// </summary>
    [Fact]
    public void Constructor_BudgetBelowTheMinimum_Throws()
    {
        var act = () => new SummaryPromptBuilder("{{RECENT_NEWS}}", new GradeCalculator(), 10);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Prior digests used to be concatenated in full and undated. Now only the newest is quoted,
    /// and everything older is reduced to a dated index line.
    /// </summary>
    [Fact]
    public void Build_OnlyTheNewestPriorDigestIsQuotedInFull()
    {
        var builder = new SummaryPromptBuilder("INDEX:\n{{COVERAGE_LEDGER}}\nLAST:\n{{LAST_DIGEST}}");

        var digests = new List<PriorDigest>
        {
            new(new DateTime(2026, 3, 1), "### Spring Concert\nTickets go on sale Monday."),
            new(new DateTime(2026, 2, 1), "### Book Fair\nVolunteers are still needed at the register.")
        };

        var prompt = builder.Build(Now, TwoChildren(), [], digests, []);

        prompt.Should().Contain("2026-03-01 digest:");
        prompt.Should().Contain("2026-02-01 digest:");
        prompt.Should().Contain("Sent 2026-03-01:");
        prompt.Should().Contain("Tickets go on sale Monday.");

        // The older digest contributes its topic to the index and nothing else.
        prompt.Should().Contain("Book Fair");
        prompt.Should().NotContain("Volunteers are still needed");
    }

    /// <summary>
    /// Verifies that both history tokens degrade to "None" for a brand new parent.
    /// </summary>
    [Fact]
    public void Build_NoPriorDigests_RendersNoneForBothHistoryTokens()
    {
        var builder = new SummaryPromptBuilder("INDEX:{{COVERAGE_LEDGER}}|LAST:{{LAST_DIGEST}}");

        var prompt = builder.Build(Now, TwoChildren(), [], [], []);

        prompt.Should().Be("INDEX:None|LAST:None");
    }

    /// <summary>
    /// Verifies that every supported token is replaced.
    /// </summary>
    [Fact]
    public void Build_AllSupportedTokensAreReplaced()
    {
        var builder = new SummaryPromptBuilder(
            "{{TODAY}}|{{SUMMARY_TITLE}}|{{CONTEXT}}|{{RECENT_NEWS}}|{{COVERAGE_LEDGER}}"
            + "|{{LAST_DIGEST}}|{{UPCOMING_DATES}}|{{SCHOOL_WIDE_SECTIONS}}|{{CHILD_SECTIONS}}");

        var prompt = builder.Build(Now, TwoChildren(), [], [], []);

        prompt.Should().NotContain("{{");
        prompt.Should().NotContain("}}");
    }

    /// <summary>
    /// The standing instructions are split out of the template so they can travel as a system
    /// prompt, leaving only this run's content in the user message.
    /// </summary>
    [Fact]
    public void Constructor_TemplateWithMarkers_SplitsStaticInstructionsFromVolatileContent()
    {
        var builder = new SummaryPromptBuilder(
            "<<<SYSTEM>>>\nStanding rules.\n<<<USER>>>\nToday: {{TODAY}}");

        builder.SystemPrompt.Should().Be("Standing rules.");
        builder.Build(Now, [], [], [], []).Should().Be("Today: Monday, March 9, 2026");
    }

    /// <summary>
    /// A template with no markers is a plain user message, which keeps single-token templates
    /// usable and keeps a provider from being handed an empty system block.
    /// </summary>
    [Fact]
    public void Constructor_TemplateWithoutMarkers_HasNoSystemPrompt()
    {
        var builder = new SummaryPromptBuilder("Today: {{TODAY}}");

        builder.SystemPrompt.Should().BeEmpty();
        builder.Build(Now, [], [], [], []).Should().Be("Today: Monday, March 9, 2026");
    }

    /// <summary>
    /// The shipped template must split cleanly, must leave no token behind, and must no longer
    /// carry the pre-computed day-by-day calendar that used to be pasted into every prompt.
    /// </summary>
    [Fact]
    public void DefaultTemplate_SplitsIntoASystemPromptAndALeftoverFreeUserMessage()
    {
        var builder = new SummaryPromptBuilder();

        builder.SystemPrompt.Should().NotBeEmpty();
        builder.SystemPrompt.Should().NotContain("{{");
        builder.SystemPrompt.Should().Contain("never been fed into a digest");

        var prompt = builder.Build(Now, TwoChildren(), [], [], []);

        prompt.Should().NotContain("{{");
        prompt.Should().NotContain("WEEK_CALENDAR");

        // The deleted generator listed every date from 14 days back to 120 days ahead.
        prompt.Should().NotContain("Monday, March 23, 2026");
        prompt.Should().NotContain("Sunday, February 22, 2026");
    }

    /// <summary>
    /// Verifies that {{TODAY}} and {{SUMMARY_TITLE}} reflect the supplied local date. The generator
    /// converts UTC to the configured timezone before calling Build, so the builder must forward
    /// whatever date it is handed.
    /// </summary>
    [Theory]
    [InlineData(2026, 5, 24, "Sunday, May 24, 2026")]
    [InlineData(2026, 5, 25, "Monday, May 25, 2026")]
    [InlineData(2026, 9, 1, "Tuesday, September 1, 2026")]
    public void Build_TodayAndTitle_ReflectSuppliedDate(int year, int month, int day, string expectedDayDate)
    {
        var builder = new SummaryPromptBuilder("{{TODAY}}|{{SUMMARY_TITLE}}");
        var now = new DateTime(year, month, day, 8, 0, 0, DateTimeKind.Unspecified);

        var prompt = builder.Build(now, [], [], [], []);

        prompt.Should().Contain(expectedDayDate);
        prompt.Should().Contain("School News Digest - " + expectedDayDate);
    }
}
