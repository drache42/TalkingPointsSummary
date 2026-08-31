using FluentAssertions;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

/// <summary>
/// Verifies the dated coverage index built from previously sent digests. The index is what
/// replaced dumping every prior digest into the prompt as undated prose, so these tests pin
/// down that it names the date of each digest, lists what that digest actually covered, and
/// stays compact no matter how long the school year runs.
/// </summary>
public class SummaryCoverageLedgerTests
{
    private const string RealDigest = """
        # School News Digest - Sunday, May 17, 2026

        # District-Wide News

        ### Bus Route Changes
        Routes 4 and 7 swap their afternoon stops starting next week.

        ## Sample Elementary School-Wide News

        ### Spring Concert
        Third through fifth grade sing at 6:30.

        # Ada (3rd Grade at Sample Elementary)

        ### Field Day
        Sneakers and a water bottle, please.

        ## Important Upcoming Dates

        ### Sample Elementary (Ada)
        - **Friday, May 22, 2026** - Spring Concert (6:30 PM)
        - **Monday, May 25, 2026** - No School
        """;

    [Fact]
    public void Extract_ListsSubheadingTopicsWithTheirSectionAndSkipsStructuralHeadings()
    {
        var entry = SummaryCoverageLedger.Extract(RealDigest);

        entry.Topics.Should().Equal(
            "District-Wide News > Bus Route Changes",
            "Sample Elementary School-Wide News > Spring Concert",
            "Ada (3rd Grade at Sample Elementary) > Field Day");
    }

    [Fact]
    public void Extract_SchoolSubheadingsInsideTheDatesSectionAreNotTopics()
    {
        // "### Sample Elementary (Ada)" is a level-3 heading, but inside the dates section it
        // names a school rather than a subject, and the dates beneath it are captured separately.
        var entry = SummaryCoverageLedger.Extract(RealDigest);

        entry.Topics.Should().NotContain(topic => topic.Contains("Sample Elementary (Ada)"));
    }

    [Fact]
    public void Extract_ListsDatedBulletLinesInDocumentOrder()
    {
        var entry = SummaryCoverageLedger.Extract(RealDigest);

        entry.DatedItems.Should().Equal(
            "Friday, May 22, 2026 - Spring Concert (6:30 PM)",
            "Monday, May 25, 2026 - No School");
    }

    [Fact]
    public void Extract_AcceptsEverySeparatorEarlierDigestsUsedBetweenDateAndEvent()
    {
        // Digests already in the database were written with an en dash, and later with an em dash.
        // The index has to read its own history, not only the format written from now on.
        var content =
            "- **May 1, 2026** \u2013 En dash event\n"
            + "- **May 2, 2026** \u2014 Em dash event\n"
            + "- **May 3, 2026**: Colon event\n"
            + "- **May 4, 2026** - Hyphen event\n"
            + "- **May 5, 2026** Bare event";

        var entry = SummaryCoverageLedger.Extract(content);

        entry.DatedItems.Should().Equal(
            "May 1, 2026 - En dash event",
            "May 2, 2026 - Em dash event",
            "May 3, 2026 - Colon event",
            "May 4, 2026 - Hyphen event",
            "May 5, 2026 - Bare event");
    }

    [Fact]
    public void Extract_IgnoresBulletsThatCarryNoBoldDate()
    {
        var content =
            "- Bring a water bottle\n"
            + "- **Reminder** for everyone\n"
            + "**Friday, May 22, 2026** - not a bullet at all";

        var entry = SummaryCoverageLedger.Extract(content);

        // "- **Reminder** for everyone" matches the bullet shape, so it is listed under the text
        // it does carry. The plain bullet and the un-bulleted bold line are not dated lines.
        entry.DatedItems.Should().Equal("Reminder - for everyone");
    }

    [Fact]
    public void Extract_CollapsesRepeatedTopics()
    {
        var content = "### Reminders\ntext\n\n### Reminders\nmore text\n\n### Lunch Menu\ntext";

        var entry = SummaryCoverageLedger.Extract(content);

        entry.Topics.Should().Equal("Reminders", "Lunch Menu");
    }

    [Fact]
    public void Extract_NullOrBlankContent_YieldsNothing()
    {
        var entry = SummaryCoverageLedger.Extract(null);

        entry.Topics.Should().BeEmpty();
        entry.DatedItems.Should().BeEmpty();
    }

    [Fact]
    public void Extract_TopicLongerThanTheEntryCap_IsShortened()
    {
        var content = "### " + new string('x', 400);

        var entry = SummaryCoverageLedger.Extract(content);

        entry.Topics.Should().ContainSingle();
        entry.Topics[0].Length.Should().Be(SummaryCoverageLedger.MaxEntryLength);
        entry.Topics[0].Should().EndWith("...");
    }

    [Fact]
    public void Render_NoPriorDigests_ReturnsNone()
    {
        SummaryCoverageLedger.Render([]).Should().Be(SummaryCoverageLedger.EmptyLedgerText);
    }

    [Fact]
    public void Render_StampsEachDigestWithItsOwnDateAndKeepsTheSuppliedOrder()
    {
        // The whole point of the ledger: last week's digest and one from five weeks ago are
        // distinguishable, which undated concatenated prose never was.
        var digests = new List<PriorDigest>
        {
            new(new DateTime(2026, 5, 17), "### Field Day\ntext\n- **Friday, May 22, 2026** - Concert"),
            new(new DateTime(2026, 4, 12), "### Book Fair\ntext")
        };

        var rendered = SummaryCoverageLedger.Render(digests);

        rendered.Should().Be(
            "2026-05-17 digest:\n"
            + "  topics: Field Day\n"
            + "  dates listed: Friday, May 22, 2026 - Concert\n"
            + "2026-04-12 digest:\n"
            + "  topics: Book Fair\n"
            + "  dates listed: (none)",
            because: "the index is a compact dated line per digest");
    }

    [Fact]
    public void Render_DigestWithNoRecognisableContent_StillGetsADatedLine()
    {
        var digests = new List<PriorDigest> { new(new DateTime(2026, 5, 17), "Just a paragraph.") };

        var rendered = SummaryCoverageLedger.Render(digests);

        rendered.Should().Contain("2026-05-17 digest:");
        rendered.Should().Contain("topics: " + SummaryCoverageLedger.EmptyListText);
        rendered.Should().Contain("dates listed: " + SummaryCoverageLedger.EmptyListText);
    }

    [Fact]
    public void Render_MoreTopicsThanTheCap_ListsTheCapAndCountsTheRest()
    {
        var overflow = 5;
        var total = SummaryCoverageLedger.MaxTopicsPerDigest + overflow;
        var content = string.Join(
            "\n",
            Enumerable.Range(1, total).Select(index => "### Topic " + index.ToString()));

        var rendered = SummaryCoverageLedger.Render([new PriorDigest(new DateTime(2026, 5, 17), content)]);

        rendered.Should().Contain("Topic " + SummaryCoverageLedger.MaxTopicsPerDigest.ToString());
        rendered.Should().NotContain("Topic " + (SummaryCoverageLedger.MaxTopicsPerDigest + 1).ToString());
        rendered.Should().Contain("(+" + overflow.ToString() + " more)");
    }

    [Fact]
    public void Render_MoreDigestsThanTheCap_StopsAtTheCap()
    {
        var digests = Enumerable
            .Range(0, SummaryCoverageLedger.MaxDigests + 3)
            .Select(offset => new PriorDigest(
                new DateTime(2026, 5, 17).AddDays(-7 * offset),
                "### Topic " + offset.ToString()))
            .ToList();

        var rendered = SummaryCoverageLedger.Render(digests);

        rendered.Split('\n').Count(line => line.EndsWith(" digest:", StringComparison.Ordinal))
            .Should().Be(SummaryCoverageLedger.MaxDigests);
    }
}
