using FluentAssertions;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class SummaryRevisionPromptBuilderTests
{
    private const string Template =
        "Today is {{TODAY}}.\nDEFECTS:\n{{ISSUES}}\nDATES:\n{{UPCOMING_DATES}}\nDRAFT:\n{{DRAFT}}";

    [Fact]
    public void Build_ReplacesEveryToken()
    {
        var builder = new SummaryRevisionPromptBuilder(Template);

        var prompt = builder.Build(
            "# Digest\nBody.",
            "1. Line 4 (WeekdayDateMismatch): May 15, 2026 falls on a Friday.",
            "### Sample Elementary\n- **Friday, March 20, 2026** - Book Fair",
            new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc));

        prompt.Should().NotContain("{{");
        prompt.Should().Contain("Today is March 9, 2026.");
        prompt.Should().Contain("1. Line 4 (WeekdayDateMismatch)");
        prompt.Should().Contain("- **Friday, March 20, 2026** - Book Fair");
        prompt.Should().Contain("# Digest\nBody.");
    }

    /// <summary>
    /// A blank upcoming-dates block must render as an explicit "None" rather than as a hole the
    /// model reads as an instruction to invent a section.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_NoUpcomingDates_RendersThePlaceholder(string? upcomingDates)
    {
        var builder = new SummaryRevisionPromptBuilder(Template);

        var prompt = builder.Build(
            "# Digest\nBody.",
            "1. Something is wrong.",
            upcomingDates,
            new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc));

        prompt.Should().Contain("DATES:\n" + SummaryRevisionPromptBuilder.EmptyPlaceholder);
    }

    [Fact]
    public void Build_NullDraft_Throws()
    {
        var builder = new SummaryRevisionPromptBuilder(Template);

        var act = () => builder.Build(null!, "1. Something is wrong.", null, DateTime.UtcNow);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_EmptyTemplate_Throws(string template)
    {
        var act = () => new SummaryRevisionPromptBuilder(template);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// The shipped template has to carry every token the builder fills, or a revision goes out
    /// with a literal placeholder where the draft should be.
    /// </summary>
    [Fact]
    public void DefaultTemplate_CarriesEveryToken()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "SummaryRevisionPromptTemplate.txt");
        File.Exists(path).Should().BeTrue();

        var template = File.ReadAllText(path);

        template.Should().Contain("{{TODAY}}");
        template.Should().Contain("{{ISSUES}}");
        template.Should().Contain("{{UPCOMING_DATES}}");
        template.Should().Contain("{{DRAFT}}");
    }

    /// <summary>
    /// The default constructor reads the shipped template off disk, which is the path production
    /// takes.
    /// </summary>
    [Fact]
    public void Build_WithTheShippedTemplate_FillsEveryToken()
    {
        var prompt = new SummaryRevisionPromptBuilder().Build(
            "# Digest\nThe assembly is on Thursday, March 12, 2026.",
            "1. Line 3 (PastUpcomingDate): March 1, 2026 is before March 10, 2026.",
            "### Sample Elementary\n- **Friday, March 20, 2026** - Book Fair",
            new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc));

        prompt.Should().NotContain("{{");
        prompt.Should().Contain("March 10, 2026");
        prompt.Should().Contain("PastUpcomingDate");
        prompt.Should().Contain("Book Fair");
        prompt.Should().Contain("The assembly is on Thursday, March 12, 2026.");
    }
}
