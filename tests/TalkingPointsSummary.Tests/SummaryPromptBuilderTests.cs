using FluentAssertions;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class SummaryPromptBuilderTests
{
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
            new() { Name = "Clara", School = "James Baldwin", StartingGrade = 0, StartingYear = 2025, Emoji = "📚" },
            new() { Name = "Nolan", School = "Cascadia", StartingGrade = 3, StartingYear = 2025, Emoji = "🎓" }
        };

        var prompt = builder.Build(
            new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc),
            children,
            [],
            []);

        prompt.Should().Contain("# 📚 Clara (Kindergarten at James Baldwin)");
        prompt.Should().Contain("# 🎓 Nolan (3rd Grade at Cascadia)");
    prompt.Should().Contain("## James Baldwin School-Wide News");
    prompt.Should().Contain("## Cascadia School-Wide News");
        prompt.Should().Contain("### James Baldwin (Clara)");
        prompt.Should().Contain("### Cascadia (Nolan)");
        prompt.Should().Contain("- **[Date]** – [Event] ([Time if applicable])");
    }

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
            new() { Name = "Clara", School = "James Baldwin", StartingGrade = 0, StartingYear = 2025, Emoji = "📚" }
        };

        var newsItems = new List<NewsItem>
        {
            new()
            {
                StudentName = "Clara",
                FromName = "Ms. Zutz",
                SourceType = SourceType.NewsletterUrl,
                SentAt = new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc),
                NewsContent = "Detailed classroom update"
            }
        };

        var previousSummaries = new List<Summary>
        {
            new() { Content = "Earlier summary" }
        };

        var prompt = builder.Build(
            new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc),
            children,
            newsItems,
            previousSummaries);

        prompt.Should().Contain("Today: Monday, March 9, 2026");
        prompt.Should().Contain("- Clara (James Baldwin) — Kindergarten");
        prompt.Should().Contain("Type: Newsletter");
        prompt.Should().Contain("Content: Detailed classroom update");
        prompt.Should().Contain("Earlier summary");
    }
}