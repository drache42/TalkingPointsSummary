using FluentAssertions;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class SummaryOutputValidatorTests
{
    // Friday, May 15, 2026. Every expectation below is anchored to this date.
    private static readonly DateTime Today = new(2026, 5, 15);

    private readonly SummaryOutputValidator _validator = new();

    private static string ProseDigest(string sentence)
        => "# School News Digest\n\n### Announcements\n" + sentence + "\n";

    private static string UpcomingDatesDigest(params string[] bulletLines)
    {
        var body = string.Join("\n", bulletLines);
        return "# School News Digest\n\n## Important Upcoming Dates\n\n### Lincoln Elementary (Ava)\n" + body + "\n";
    }

    private List<SummaryValidationFinding> Findings(string markdown, SummaryValidationFindingKind kind)
        => _validator.Validate(markdown, Today).Where(f => f.Kind == kind).ToList();

    [Theory]
    // May 15, 2026 is a Friday; June 1, 2026 is a Monday; December 18, 2026 is a Friday.
    [InlineData("Friday, May 15, 2026", false)]
    [InlineData("Thursday, May 15, 2026", true)]
    [InlineData("Monday, June 1, 2026", false)]
    [InlineData("Tuesday, June 1, 2026", true)]
    [InlineData("Mon, June 1, 2026", false)]
    [InlineData("Tues, June 1, 2026", true)]
    [InlineData("Sunday, May 31, 2026", false)]
    [InlineData("Monday, December 18, 2026", true)]
    [InlineData("May 15, 2026", false)]
    public void Validate_WeekdayAgreement_FlagsOnlyDisagreement(string dateText, bool expectMismatch)
    {
        var markdown = ProseDigest("The science fair is on **" + dateText + "** in the gym.");

        var findings = Findings(markdown, SummaryValidationFindingKind.WeekdayDateMismatch);

        findings.Should().HaveCount(expectMismatch ? 1 : 0);
    }

    [Fact]
    public void Validate_WeekdayMismatch_MessageNamesBothWeekdays()
    {
        var markdown = ProseDigest("Chorus concert on **Thursday, May 15, 2026**.");

        var finding = Findings(markdown, SummaryValidationFindingKind.WeekdayDateMismatch).Should().ContainSingle().Subject;

        finding.Message.Should().Contain("falls on a Friday");
        finding.Message.Should().Contain("not a Thursday");
        finding.Excerpt.Should().Be("Chorus concert on **Thursday, May 15, 2026**.");
        finding.LineNumber.Should().Be(4);
    }

    [Theory]
    [InlineData("May 14, 2026", true)]
    [InlineData("April 30, 2026", true)]
    [InlineData("December 18, 2025", true)]
    [InlineData("May 15, 2026", false)]
    [InlineData("May 16, 2026", false)]
    [InlineData("June 1, 2026", false)]
    public void Validate_UpcomingDatesSection_FlagsOnlyPastDates(string dateText, bool expectPast)
    {
        var markdown = UpcomingDatesDigest("- **" + dateText + "** - Book fair");

        var findings = Findings(markdown, SummaryValidationFindingKind.PastUpcomingDate);

        findings.Should().HaveCount(expectPast ? 1 : 0);
    }

    [Fact]
    public void Validate_PastDateOutsideUpcomingSection_IsNotFlagged()
    {
        var markdown = ProseDigest("The class finished its poetry unit on **Wednesday, May 13, 2026**.");

        Findings(markdown, SummaryValidationFindingKind.PastUpcomingDate).Should().BeEmpty();
    }

    [Fact]
    public void Validate_UpcomingSectionEndsAtNextSameLevelHeading()
    {
        var markdown = string.Join("\n",
            "## Important Upcoming Dates",
            "",
            "### Lincoln Elementary (Ava)",
            "- **May 20, 2026** - Field day",
            "",
            "## Questions",
            "Return the May 12, 2026 permission form if you have not already.",
            "");

        Findings(markdown, SummaryValidationFindingKind.PastUpcomingDate).Should().BeEmpty();
    }

    [Fact]
    public void Validate_PastUpcomingDate_MessageNamesTheDateAndToday()
    {
        var markdown = UpcomingDatesDigest("- **May 12, 2026** - Book fair");

        var finding = Findings(markdown, SummaryValidationFindingKind.PastUpcomingDate).Should().ContainSingle().Subject;

        finding.Message.Should().Contain("May 12, 2026");
        finding.Message.Should().Contain("May 15, 2026");
    }

    [Theory]
    [InlineData("today", true)]
    [InlineData("Today", true)]
    [InlineData("tomorrow", true)]
    [InlineData("yesterday", true)]
    [InlineData("tonight", true)]
    [InlineData("this week", true)]
    [InlineData("This week", true)]
    [InlineData("next week", true)]
    [InlineData("last week", true)]
    [InlineData("this weekend", true)]
    [InlineData("this morning", true)]
    [InlineData("this Friday", true)]
    [InlineData("next Friday", true)]
    [InlineData("next Mon", true)]
    [InlineData("the coming Monday", true)]
    [InlineData("next month", true)]
    [InlineData("Friday, May 15, 2026", false)]
    [InlineData("Monday, June 1, 2026", false)]
    [InlineData("this year's talent show", false)]
    [InlineData("the last day of school", false)]
    [InlineData("Sunday services", false)]
    [InlineData("the Upcoming Dates section", false)]
    public void Validate_RelativeTerms_FlaggedOnlyWhenPresent(string phrase, bool expectFinding)
    {
        var markdown = ProseDigest("Please remember that " + phrase + " is picture day.");

        var findings = Findings(markdown, SummaryValidationFindingKind.RelativeDateTerm);

        findings.Should().HaveCount(expectFinding ? 1 : 0);
    }

    [Fact]
    public void Validate_RelativeTerm_MessageQuotesTheTerm()
    {
        var markdown = ProseDigest("Field day is next Friday, so pack water.");

        var finding = Findings(markdown, SummaryValidationFindingKind.RelativeDateTerm).Should().ContainSingle().Subject;

        finding.Message.Should().Contain("\"next Friday\"");
    }

    [Fact]
    public void Validate_MultipleRelativeTermsOnOneLine_AreEachReported()
    {
        var markdown = ProseDigest("Forms went home yesterday and are due tomorrow.");

        var findings = Findings(markdown, SummaryValidationFindingKind.RelativeDateTerm);

        findings.Should().HaveCount(2);
        findings.Should().OnlyContain(f => f.LineNumber == 4);
    }

    [Theory]
    [InlineData("See the [sign-up form](https://school.example.com/register-today) for details.")]
    [InlineData("Register at https://school.example.com/events/today-only before the deadline.")]
    public void Validate_RelativeWordsInsideLinks_AreIgnored(string sentence)
    {
        var markdown = ProseDigest(sentence);

        Findings(markdown, SummaryValidationFindingKind.RelativeDateTerm).Should().BeEmpty();
    }

    [Fact]
    public void Validate_RelativeWordInLinkText_IsStillReported()
    {
        var markdown = ProseDigest("Use the [order lunch for tomorrow](https://school.example.com/lunch) form.");

        Findings(markdown, SummaryValidationFindingKind.RelativeDateTerm).Should().ContainSingle();
    }

    [Fact]
    public void Validate_DatesOutOfOrderWithinSubsection_AreFlagged()
    {
        var markdown = UpcomingDatesDigest(
            "- **May 20, 2026** - Chorus concert",
            "- **May 18, 2026** - Permission slips due",
            "- **May 22, 2026** - Field day");

        var findings = Findings(markdown, SummaryValidationFindingKind.OutOfOrderDates);

        var finding = findings.Should().ContainSingle().Subject;
        finding.Excerpt.Should().Be("- **May 18, 2026** - Permission slips due");
        finding.Message.Should().Contain("May 20, 2026");
    }

    [Fact]
    public void Validate_DatesInChronologicalOrder_ProduceNoFindings()
    {
        var markdown = UpcomingDatesDigest(
            "- **May 18, 2026** - Permission slips due",
            "- **May 22, 2026** - Field day",
            "- **June 1, 2026** - Last day of school");

        _validator.Validate(markdown, Today).Should().BeEmpty();
    }

    [Fact]
    public void Validate_SameDateTwiceInOrder_IsNotOutOfOrder()
    {
        var markdown = UpcomingDatesDigest(
            "- **May 20, 2026** - Chorus concert",
            "- **May 20, 2026** - Bake sale");

        Findings(markdown, SummaryValidationFindingKind.OutOfOrderDates).Should().BeEmpty();
    }

    [Fact]
    public void Validate_OrderingRestartsForEachSchoolSubsection()
    {
        var markdown = string.Join("\n",
            "## Important Upcoming Dates",
            "",
            "### Lincoln Elementary (Ava)",
            "- **June 5, 2026** - Field day",
            "",
            "### Jefferson Middle (Ben)",
            "- **May 20, 2026** - Band concert",
            "");

        Findings(markdown, SummaryValidationFindingKind.OutOfOrderDates).Should().BeEmpty();
    }

    [Fact]
    public void Validate_SecondDateOnALineDoesNotDriveOrdering()
    {
        var markdown = UpcomingDatesDigest(
            "- **May 18, 2026** - Field trip (rain date June 5, 2026)",
            "- **May 22, 2026** - Field day");

        Findings(markdown, SummaryValidationFindingKind.OutOfOrderDates).Should().BeEmpty();
    }

    [Theory]
    // Today plus 18 months is November 15, 2027.
    [InlineData("June 1, 2026", false)]
    [InlineData("November 15, 2027", false)]
    [InlineData("November 16, 2027", true)]
    [InlineData("June 1, 2028", true)]
    [InlineData("June 1, 2126", true)]
    public void Validate_ImplausiblyDistantDates_AreFlagged(string dateText, bool expectFinding)
    {
        var markdown = ProseDigest("Registration opens for " + dateText + ".");

        var findings = Findings(markdown, SummaryValidationFindingKind.ImplausiblyDistantDate);

        findings.Should().HaveCount(expectFinding ? 1 : 0);
    }

    [Fact]
    public void Validate_ImplausiblyDistantDate_MessageMentionsTheHorizon()
    {
        var markdown = ProseDigest("Registration opens for June 1, 2028.");

        var finding = Findings(markdown, SummaryValidationFindingKind.ImplausiblyDistantDate).Should().ContainSingle().Subject;

        finding.Message.Should().Contain("18 months");
        finding.Message.Should().Contain("June 1, 2028");
    }

    [Theory]
    [InlineData("May 15", true)]
    [InlineData("Friday, May 15", true)]
    [InlineData("June 5", true)]
    [InlineData("Jan 8", true)]
    [InlineData("May 15, 2026", false)]
    [InlineData("Friday, May 15, 2026", false)]
    public void Validate_DatesWithoutAYear_AreReportedNotGuessed(string dateText, bool expectFinding)
    {
        var markdown = ProseDigest("The plant sale is on **" + dateText + "** in the courtyard.");

        var findings = Findings(markdown, SummaryValidationFindingKind.MissingYear);

        findings.Should().HaveCount(expectFinding ? 1 : 0);
    }

    [Fact]
    public void Validate_YearlessDate_WeekdayThatFitsANearbyYear_IsNotFlagged()
    {
        // May 15 is a Friday in 2026 but a Thursday in 2025. Without a year the validator must
        // report the missing year rather than accuse a weekday that could well be right.
        var markdown = ProseDigest("Chorus concert on **Thursday, May 15**.");

        var findings = _validator.Validate(markdown, Today);

        findings.Should().ContainSingle();
        findings[0].Kind.Should().Be(SummaryValidationFindingKind.MissingYear);
    }

    [Fact]
    public void Validate_YearlessDate_WeekdayWrongInEveryNearbyYear_IsStillFlagged()
    {
        // May 15 is a Thursday in 2025, a Friday in 2026 and a Saturday in 2027, so "Monday" is
        // wrong however the missing year is read. A missing year must not suppress a real error.
        var markdown = ProseDigest("Chorus concert on **Monday, May 15**.");

        var findings = _validator.Validate(markdown, Today);

        findings.Select(f => f.Kind).Should().Equal(
            SummaryValidationFindingKind.MissingYear,
            SummaryValidationFindingKind.WeekdayDateMismatch);
        findings[1].Message.Should().Contain("May 15 does not fall on a Monday");
    }

    [Fact]
    public void Validate_YearlessImpossibleDate_IsFlaggedWithoutAYear()
    {
        var markdown = ProseDigest("The deadline is February 30.");

        var findings = _validator.Validate(markdown, Today);

        findings.Select(f => f.Kind).Should().Equal(
            SummaryValidationFindingKind.MissingYear,
            SummaryValidationFindingKind.InvalidDate);
    }

    [Fact]
    public void Validate_YearlessLeapDay_IsNotCalledInvalid()
    {
        // No year near 2026 is a leap year, but "February 29" is a real date in 2028 and the
        // validator refuses to call it impossible on the strength of a year it was not given.
        var markdown = ProseDigest("The deadline is February 29.");

        var findings = _validator.Validate(markdown, Today);

        findings.Should().ContainSingle();
        findings[0].Kind.Should().Be(SummaryValidationFindingKind.MissingYear);
    }

    [Fact]
    public void Validate_YearlessUpcomingDatesInOrderAcrossTheYearBoundary_AreNotFlagged()
    {
        // Read as their next occurrence, December 20 comes before January 8. Reading both as the
        // current year would report a false ordering error.
        var december = new DateTime(2026, 12, 18);
        var markdown = string.Join("\n",
            "## Important Upcoming Dates",
            "",
            "### Lincoln Elementary (Ava)",
            "- **December 20** - Winter concert",
            "- **January 8** - Classes resume",
            "");

        var findings = _validator.Validate(markdown, december);

        findings.Should().OnlyContain(f => f.Kind == SummaryValidationFindingKind.MissingYear);
    }

    [Fact]
    public void Validate_YearlessUpcomingDatesOutOfOrder_AreStillFlagged()
    {
        var december = new DateTime(2026, 12, 18);
        var markdown = string.Join("\n",
            "## Important Upcoming Dates",
            "",
            "### Lincoln Elementary (Ava)",
            "- **January 8** - Classes resume",
            "- **December 20** - Winter concert",
            "");

        var findings = _validator.Validate(markdown, december)
            .Where(f => f.Kind == SummaryValidationFindingKind.OutOfOrderDates)
            .ToList();

        findings.Should().ContainSingle().Which.LineNumber.Should().Be(5);
    }

    [Theory]
    // The year sits at the end of the range, not directly after the first day.
    [InlineData("- **Book Fair**: May 15-16, 2026")]
    [InlineData("- Spirit Week: May 11 - 15, 2026")]
    [InlineData("- Testing window May 4-8, 2026")]
    [InlineData("- Conferences: May 15 and 16, 2026")]
    [InlineData("- Half day May 15/16, 2026")]
    [InlineData("- Spring break: May 4 through 8, 2026")]
    // The second date repeats the month, or the weekday and the month. Both are ordinary school
    // phrasing, and the model cannot satisfy a MissingYear complaint here without writing the
    // unnatural "May 30, 2026 - June 2, 2026".
    [InlineData("- **May 30 - June 2, 2026** - Memorial Break")]
    [InlineData("- **Thursday, October 29 and Friday, October 30, 2026** - Conferences")]
    [InlineData("- Testing: Monday, June 1 through Friday, June 5, 2026")]
    public void Validate_DateRangeCarryingItsYear_IsNotReportedAsMissingAYear(string line)
    {
        var markdown = UpcomingDatesDigest(line);

        Findings(markdown, SummaryValidationFindingKind.MissingYear).Should().BeEmpty();
    }

    [Fact]
    public void Validate_PairOfDatesSharingOneTrailingYear_IsAcceptedWhole()
    {
        // October 30, 2025 is a Thursday and October 31 is a Friday, so this line is correct and
        // must produce nothing at all.
        var markdown = ProseDigest("Halloween runs **Thursday, October 30 and Friday, October 31, 2025**.");

        _validator.Validate(markdown, Today).Should().BeEmpty();
    }

    [Fact]
    public void Validate_SecondDateOfAPair_IsStillCheckedAgainstItsOwnWeekday()
    {
        // October 31, 2025 is a Friday. Swallowing the whole range into the first date's match
        // would leave every date after the first unvalidated.
        var markdown = ProseDigest("Halloween runs **Thursday, October 30 and Saturday, October 31, 2025**.");

        var findings = _validator.Validate(markdown, Today);

        findings.Should().ContainSingle();
        findings[0].Kind.Should().Be(SummaryValidationFindingKind.WeekdayDateMismatch);
        findings[0].Message.Should().Contain("Saturday, October 31, 2025");
    }

    [Fact]
    public void Validate_RangeWrappingIntoAnEarlierMonth_DoesNotBorrowTheTrailingYear()
    {
        // "December 20 through January 5, 2027" states 2027 for January only: December 20 is 2026.
        // Reading the trailing year onto December would turn a Sunday into a Monday and invent a
        // weekday mismatch, so the honest report is that December's year is missing.
        var markdown = ProseDigest("Winter break runs **Sunday, December 20 through Tuesday, January 5, 2027**.");

        var findings = _validator.Validate(markdown, Today);

        findings.Should().ContainSingle();
        findings[0].Kind.Should().Be(SummaryValidationFindingKind.MissingYear);
        findings[0].Message.Should().Contain("\"Sunday, December 20\"");
    }

    [Fact]
    public void Validate_PastDateRangeUnderUpcomingDates_IsFlagged()
    {
        // The year is present, just at the far end of the range, so the past-date rule must run.
        var markdown = UpcomingDatesDigest("- **Book Fair**: April 6-10, 2026");

        var findings = _validator.Validate(markdown, Today);

        findings.Should().ContainSingle();
        findings[0].Kind.Should().Be(SummaryValidationFindingKind.PastUpcomingDate);
        findings[0].Message.Should().Contain("April 6-10, 2026");
    }

    [Fact]
    public void Validate_RangeWithNoYear_IsStillReportedAsMissingAYear()
    {
        var markdown = UpcomingDatesDigest("- **Book Fair**: May 18-19");

        Findings(markdown, SummaryValidationFindingKind.MissingYear).Should().ContainSingle();
    }

    [Fact]
    public void Validate_SeparateDatesOnOneLine_DoNotBorrowEachOthersYear()
    {
        // "May 20" is followed by a different month, so the trailing year belongs to June 3 only.
        var markdown = ProseDigest("Forms are due May 20 and the trip is June 3, 2026.");

        var findings = Findings(markdown, SummaryValidationFindingKind.MissingYear);

        findings.Should().ContainSingle();
        findings[0].Message.Should().Contain("\"May 20\"");
    }

    [Fact]
    public void ShippedSummaryPromptTemplate_DemandsTheYearTheValidatorRequires()
    {
        // The MissingYear rule is only fair if the prompt actually asks for a year. If the shipped
        // template models a bare "May 15", every compliant digest is reported as broken.
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", "SummaryPromptTemplate.txt");
        File.Exists(path).Should().BeTrue("the worker ships the digest prompt template");

        var template = File.ReadAllText(path);

        template.Should().Contain("four-digit year");
        Findings(template, SummaryValidationFindingKind.MissingYear).Should().BeEmpty(
            "no worked example in the prompt may show a date without its year");
    }

    [Fact]
    public void Validate_YearlessDateAcrossYearBoundary_IsNotJudgedAsPast()
    {
        // A December digest listing "January 8" means next year. Guessing the current year would
        // wrongly flag it as past, so the validator reports the missing year only.
        var december = new DateTime(2026, 12, 18);
        var markdown = "## Important Upcoming Dates\n\n### Lincoln Elementary (Ava)\n- **January 8** - Winter concert\n";

        var findings = _validator.Validate(markdown, december);

        findings.Should().ContainSingle();
        findings[0].Kind.Should().Be(SummaryValidationFindingKind.MissingYear);
        findings[0].Message.Should().Contain("January 8");
    }

    [Theory]
    [InlineData("February 30, 2026", true)]
    [InlineData("February 29, 2026", true)]
    [InlineData("April 31, 2026", true)]
    [InlineData("February 29, 2028", false)]
    [InlineData("April 30, 2026", false)]
    public void Validate_ImpossibleCalendarDates_AreFlagged(string dateText, bool expectFinding)
    {
        var markdown = ProseDigest("The deadline is " + dateText + ".");

        var findings = Findings(markdown, SummaryValidationFindingKind.InvalidDate);

        findings.Should().HaveCount(expectFinding ? 1 : 0);
    }

    [Fact]
    public void Validate_LowercaseMonthWord_IsNotTreatedAsADate()
    {
        var markdown = ProseDigest("Students may 15 minutes of extra reading count toward the challenge.");

        _validator.Validate(markdown, Today).Should().BeEmpty();
    }

    [Fact]
    public void Validate_CleanDigest_ProducesNoFindings()
    {
        var markdown = string.Join("\n",
            "# School News Digest - Friday, May 15, 2026",
            "",
            "# District-Wide News",
            "",
            "### Bus Route Changes",
            "Routes shift for the final month of the school year.",
            "",
            "## Lincoln Elementary School-Wide News",
            "",
            "### Spring Concert",
            "The concert recording is posted for families who could not attend.",
            "",
            "# Ava (3rd Grade at Lincoln Elementary)",
            "",
            "### Reading Unit",
            "Ava's class wrapped up its poetry unit on **Wednesday, May 13, 2026**.",
            "",
            "## Important Upcoming Dates",
            "",
            "### Lincoln Elementary (Ava)",
            "- **Monday, May 18, 2026** - Permission slips due",
            "- **Friday, May 22, 2026** - Field day (9:00 AM)",
            "- **Monday, June 1, 2026** - Last day of school",
            "");

        _validator.Validate(markdown, Today).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ReportsFindingsInDocumentOrder()
    {
        var markdown = string.Join("\n",
            "# School News Digest",
            "",
            "### Announcements",
            "Picture retakes are tomorrow.",
            "The book fair opened on **Monday, May 20, 2026**.",
            "",
            "## Important Upcoming Dates",
            "",
            "### Lincoln Elementary (Ava)",
            "- **May 12, 2026** - Bake sale",
            "");

        var findings = _validator.Validate(markdown, Today);

        findings.Select(f => f.Kind).Should().Equal(
            SummaryValidationFindingKind.RelativeDateTerm,
            SummaryValidationFindingKind.WeekdayDateMismatch,
            SummaryValidationFindingKind.PastUpcomingDate);
        findings.Select(f => f.LineNumber).Should().Equal(4, 5, 10);
    }

    [Fact]
    public void Validate_WindowsLineEndings_ProduceCorrectLineNumbers()
    {
        var markdown = "# School News Digest\r\n\r\n### Announcements\r\nPicture retakes are tomorrow.\r\n";

        var findings = _validator.Validate(markdown, Today);

        findings.Should().ContainSingle();
        findings[0].LineNumber.Should().Be(4);
        findings[0].Excerpt.Should().Be("Picture retakes are tomorrow.");
    }

    [Fact]
    public void Validate_LongLine_TruncatesExcerpt()
    {
        var filler = new string('x', 400);
        var markdown = ProseDigest("Picture retakes are tomorrow. " + filler);

        var finding = _validator.Validate(markdown, Today).Should().ContainSingle().Subject;

        finding.Excerpt.Should().HaveLength(163);
        finding.Excerpt.Should().EndWith("...");
        finding.Excerpt.Should().StartWith("Picture retakes are tomorrow.");
    }

    [Fact]
    public void Validate_ExcerptCollapsesWhitespace()
    {
        var markdown = ProseDigest("Picture   retakes\tare  tomorrow.");

        var finding = _validator.Validate(markdown, Today).Should().ContainSingle().Subject;

        finding.Excerpt.Should().Be("Picture retakes are tomorrow.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n   \n")]
    public void Validate_EmptyOrWhitespaceMarkdown_ProducesNoFindings(string markdown)
    {
        _validator.Validate(markdown, Today).Should().BeEmpty();
    }

    [Fact]
    public void Validate_NullMarkdown_Throws()
    {
        var act = () => _validator.Validate(null!, Today);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Validate_IgnoresTimeComponentOfToday()
    {
        var markdown = UpcomingDatesDigest("- **May 15, 2026** - Field day");

        var findings = _validator.Validate(markdown, new DateTime(2026, 5, 15, 23, 59, 0));

        findings.Should().BeEmpty();
    }

    [Fact]
    public void Describe_IncludesLineKindMessageAndExcerpt()
    {
        var finding = new SummaryValidationFinding(
            SummaryValidationFindingKind.PastUpcomingDate,
            7,
            "- **May 12, 2026** - Bake sale",
            "It is in the past.");

        finding.Describe().Should().Be(
            "Line 7 (PastUpcomingDate): It is in the past. Text: \"- **May 12, 2026** - Bake sale\"");
    }

    [Fact]
    public void FormatForRevisionPrompt_NumbersEachFinding()
    {
        var findings = new[]
        {
            new SummaryValidationFinding(SummaryValidationFindingKind.RelativeDateTerm, 4, "line four", "First problem."),
            new SummaryValidationFinding(SummaryValidationFindingKind.PastUpcomingDate, 9, "line nine", "Second problem.")
        };

        var text = SummaryOutputValidator.FormatForRevisionPrompt(findings);

        var lines = text.Split('\n');
        lines.Should().HaveCount(2);
        lines[0].Should().Be("1. Line 4 (RelativeDateTerm): First problem. Text: \"line four\"");
        lines[1].Should().Be("2. Line 9 (PastUpcomingDate): Second problem. Text: \"line nine\"");
    }

    [Fact]
    public void FormatForRevisionPrompt_NoFindings_ReturnsEmptyString()
    {
        SummaryOutputValidator.FormatForRevisionPrompt(Array.Empty<SummaryValidationFinding>())
            .Should().BeEmpty();
    }

    [Fact]
    public void FormatForRevisionPrompt_NullFindings_Throws()
    {
        var act = () => SummaryOutputValidator.FormatForRevisionPrompt(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
