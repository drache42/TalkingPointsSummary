using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class SummaryCriticTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 3, 9, 8, 0, 0, TimeSpan.Zero);

    private const string Draft = """
        # School News Digest

        Picture day is on Friday, March 13.
        """;

    private readonly Mock<IAiClient> _mockAiClient = new();
    private readonly FixedTimeProvider _timeProvider = new(FixedNow);

    private AiCompletionRequest? _capturedRequest;

    [Fact]
    public async Task CritiqueAsync_WellFormedJson_ReturnsParsedFindings()
    {
        RespondWith("""
            {
              "findings": [
                {
                  "severity": "high",
                  "kind": "unresolved-relative-date",
                  "quote": "Picture day is on Friday, March 13.",
                  "problem": "Source item 1 was sent Monday, March 2 and says 'next Friday', which is March 6.",
                  "suggested_fix": "Picture day is on Friday, March 6."
                },
                {
                  "severity": "low",
                  "kind": "repeat",
                  "quote": "The book fair runs all week.",
                  "problem": "The coverage ledger shows this was already sent last week.",
                  "suggested_fix": "Drop the paragraph."
                }
              ]
            }
            """);

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().HaveCount(2);

        findings[0].Severity.Should().Be(CritiqueSeverity.High);
        findings[0].Kind.Should().Be(CritiqueFindingKinds.UnresolvedRelativeDate);
        findings[0].Quote.Should().Be("Picture day is on Friday, March 13.");
        findings[0].Problem.Should().Contain("next Friday");
        findings[0].SuggestedFix.Should().Be("Picture day is on Friday, March 6.");

        findings[1].Severity.Should().Be(CritiqueSeverity.Low);
        findings[1].Kind.Should().Be(CritiqueFindingKinds.Repeat);
        findings[1].SuggestedFix.Should().Be("Drop the paragraph.");
    }

    [Fact]
    public async Task CritiqueAsync_OmittedItemWithAValidSourceItemNumber_IsParsedAndForcedToLowSeverity()
    {
        // The model is asked to report every omitted-item finding as "low", but severity is
        // forced in code rather than trusted: this is bookkeeping, not a defect, and must never be
        // able to reach the revision decision by arriving as high or medium.
        RespondWith("""
            {
              "findings": [
                {
                  "severity": "high",
                  "kind": "omitted-item",
                  "quote": "",
                  "problem": "Source item 2, about the book fair, has no trace anywhere in the draft.",
                  "suggested_fix": "",
                  "source_item_number": 2
                }
              ]
            }
            """);

        var request = new SummaryCritiqueRequest([CreateNewsItem(), CreateNewsItem()], Draft);
        var findings = await CreateCritic().CritiqueAsync(request);

        findings.Should().ContainSingle();
        findings[0].Kind.Should().Be(CritiqueFindingKinds.OmittedItem);
        findings[0].Severity.Should().Be(CritiqueSeverity.Low, "an omission is bookkeeping, never a revisable defect");
        findings[0].SourceItemNumber.Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public async Task CritiqueAsync_OmittedItemWithASourceItemNumberOutOfRange_IsDropped(int sourceItemNumber)
    {
        // Two source items are in the request, so only 1 and 2 are real references. A number
        // outside that range is a hallucination, and keeping it would risk leaving the wrong news
        // item, or one that does not exist, unmarked as reported.
        RespondWith($$"""
            {
              "findings": [
                {
                  "severity": "low",
                  "kind": "omitted-item",
                  "quote": "",
                  "problem": "Source item claims to be omitted.",
                  "suggested_fix": "",
                  "source_item_number": {{sourceItemNumber}}
                }
              ]
            }
            """);

        var request = new SummaryCritiqueRequest([CreateNewsItem(), CreateNewsItem()], Draft);
        var findings = await CreateCritic().CritiqueAsync(request);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task CritiqueAsync_OmittedItemWithNoSourceItemNumber_IsDropped()
    {
        RespondWith("""
            {
              "findings": [
                {
                  "severity": "low",
                  "kind": "omitted-item",
                  "quote": "",
                  "problem": "Source item claims to be omitted but names no number.",
                  "suggested_fix": ""
                }
              ]
            }
            """);

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task CritiqueAsync_NonOmittedFinding_LeavesSourceItemNumberNull()
    {
        RespondWith("""
            {
              "findings": [
                {
                  "severity": "medium",
                  "kind": "wrong-attribution",
                  "quote": "Kid Two's picture day",
                  "problem": "The source item names Kid One, not Kid Two.",
                  "suggested_fix": "Kid One's picture day"
                }
              ]
            }
            """);

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().ContainSingle();
        findings[0].SourceItemNumber.Should().BeNull();
    }

    [Fact]
    public async Task CritiqueAsync_JsonWrappedInCodeFences_ReturnsParsedFindings()
    {
        // Models routinely wrap JSON in a markdown fence even when told not to. The fence is not
        // a malformed answer and must not cost the run its findings.
        RespondWith("""
            ```json
            {
              "findings": [
                {
                  "severity": "medium",
                  "kind": "wrong-attribution",
                  "quote": "Field trip for Kid Two",
                  "problem": "Source item 1 names Kid One, not Kid Two.",
                  "suggested_fix": "Move the field trip under Kid One."
                }
              ]
            }
            ```
            """);

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().ContainSingle();
        findings[0].Kind.Should().Be(CritiqueFindingKinds.WrongAttribution);
        findings[0].Severity.Should().Be(CritiqueSeverity.Medium);
        findings[0].Quote.Should().Be("Field trip for Kid Two");
    }

    [Fact]
    public async Task CritiqueAsync_EmptyFindingsArray_ReturnsNoFindings()
    {
        RespondWith("""{"findings": []}""");

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task CritiqueAsync_MalformedJson_ReturnsNoFindingsWithoutThrowing()
    {
        // The critic is advisory. An unparseable answer must leave the digest alone, not abort
        // the run that already produced it.
        RespondWith("Here are my thoughts: the digest looks fine to me, mostly.");

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task CritiqueAsync_TruncatedJson_ReturnsNoFindingsWithoutThrowing()
    {
        RespondWith("""{"findings": [{"severity": "high", "kind": "repeat", "quote": "cut off""");

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task CritiqueAsync_ProviderThrows_ReturnsNoFindingsWithoutThrowing()
    {
        _mockAiClient
            .Setup(client => client.CompleteAsync(It.IsAny<AiCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("502 Bad Gateway"));

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task CritiqueAsync_RequestTimesOut_ReturnsNoFindingsWithoutThrowing()
    {
        // An HttpClient timeout surfaces as a cancelled task even though nobody asked to cancel.
        // The caller's token is untouched, so this is a critic failure to absorb, not a shutdown.
        _mockAiClient
            .Setup(client => client.CompleteAsync(It.IsAny<AiCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request timed out.", new TimeoutException()));

        var findings = await CreateCritic().CritiqueAsync(CreateRequest(), CancellationToken.None);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task CritiqueAsync_CallerCancels_PropagatesTheCancellation()
    {
        // A shutdown is not a critic failure to swallow: the whole run is going away.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _mockAiClient
            .Setup(client => client.CompleteAsync(It.IsAny<AiCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var critic = CreateCritic();

        var act = async () => await critic.CritiqueAsync(CreateRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CritiqueAsync_ResponseWithNoText_ReturnsNoFindings()
    {
        // Observed live: the first content block came back as a thinking block with empty text.
        RespondWith(string.Empty);

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task CritiqueAsync_ResponseTruncatedAtTheTokenLimit_ReturnsNoFindings()
    {
        RespondWith("""{"findings": [{"severity": "high", "kind": "repeat", "problem": "x"}]}""", "max_tokens");

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        // A critique cut off at the ceiling is missing the findings that did not fit, and there is
        // no way to tell which. It is discarded rather than acted on, and the digest still goes.
        findings.Should().BeEmpty();
    }

    /// <summary>
    /// A refusal is prose about declining, not a findings document. It parses to nothing anyway,
    /// but reading the stop reason is what makes the log say why the digest went out unreviewed
    /// instead of reporting a clean review.
    /// </summary>
    [Fact]
    public async Task CritiqueAsync_ModelRefuses_ReturnsNoFindings()
    {
        RespondWith("I can't help with reviewing this content.", "refusal");

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task CritiqueAsync_SourceItemLongerThanTheBudget_IsCappedInThePrompt()
    {
        RespondWith("""{"findings": []}""");

        var request = CreateRequest();
        var overflow = SummaryCritiquePromptBuilder.MaxSourceContentChars + 400;
        request.SourceItems[0].NewsContent = new string('x', overflow);

        await CreateCritic().CritiqueAsync(request);

        // Unbounded here, the review request would be larger than the digest request it reviews and
        // would fail on exactly the biggest runs. Every critic failure is swallowed into "no
        // findings", so the digest would go out unreviewed precisely when it needs reviewing most.
        _capturedRequest!.Prompt.Should().NotContain(new string('x', overflow));
        _capturedRequest.Prompt.Should().Contain(
            new string('x', SummaryCritiquePromptBuilder.MaxSourceContentChars));
        _capturedRequest.Prompt.Should().Contain("truncated: 400 of");
    }

    [Fact]
    public async Task CritiqueAsync_UnknownSeverityAndKind_KeepsTheFinding()
    {
        // A vocabulary the prompt never asked for must not silently delete a real defect.
        RespondWith("""
            {
              "findings": [
                {
                  "severity": "catastrophic",
                  "kind": "hallucinated-principal",
                  "quote": "Principal Nobody will host",
                  "problem": "No source item mentions a principal by that name.",
                  "suggested_fix": "Remove the name."
                }
              ]
            }
            """);

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(CritiqueSeverity.Medium);
        findings[0].Kind.Should().Be("hallucinated-principal");
        findings[0].Problem.Should().Be("No source item mentions a principal by that name.");
    }

    [Fact]
    public async Task CritiqueAsync_FindingWithNoProblem_IsDropped()
    {
        // A finding that says nothing is wrong would push the reviser into rewriting good text.
        RespondWith("""
            {
              "findings": [
                { "severity": "high", "kind": "repeat", "quote": "something", "problem": "   " },
                { "severity": "high", "kind": "repeat", "quote": "other", "problem": "Already sent." }
              ]
            }
            """);

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().ContainSingle();
        findings[0].Quote.Should().Be("other");
    }

    [Fact]
    public async Task CritiqueAsync_MissingQuoteAndFix_DefaultsThemToEmptyStrings()
    {
        RespondWith("""
            {
              "findings": [
                { "severity": "high", "kind": "conflicting-event", "problem": "Two dates for one concert." }
              ]
            }
            """);

        var findings = await CreateCritic().CritiqueAsync(CreateRequest());

        findings.Should().ContainSingle();
        findings[0].Kind.Should().Be(CritiqueFindingKinds.ConflictingEvent);
        findings[0].Quote.Should().BeEmpty();
        findings[0].SuggestedFix.Should().BeEmpty();
    }

    [Fact]
    public async Task CritiqueAsync_EmptyDraft_ReturnsNoFindingsWithoutCallingTheModel()
    {
        RespondWith("""{"findings": []}""");

        var findings = await CreateCritic().CritiqueAsync(
            new SummaryCritiqueRequest([CreateNewsItem()], "   "));

        findings.Should().BeEmpty();
        _capturedRequest.Should().BeNull("there is nothing to review, so no tokens should be spent");
    }

    [Fact]
    public async Task CritiqueAsync_PromptCarriesTheSourceItemsAndTheDraft()
    {
        RespondWith("""{"findings": []}""");

        var newsItem = CreateNewsItem(
            newsContent: "Reminder: picture day is next Friday, so please send a comb.",
            aiSummary: "Picture day reminder",
            fromName: "Ms. Smith",
            studentName: "Kid One");

        await CreateCritic().CritiqueAsync(new SummaryCritiqueRequest(
            [newsItem],
            Draft,
            ActiveEvents: "- 2026-03-06 (9:00 AM) Picture Day",
            CoverageLedger: "Week of 2026-02-23: book fair announcement"));

        var prompt = _capturedRequest!.Prompt;

        // The source prose is the only thing that can prove a relative date was resolved wrong,
        // so it has to reach the critic verbatim alongside the draft it is checking.
        prompt.Should().Contain("Reminder: picture day is next Friday, so please send a comb.");
        prompt.Should().Contain("One-line summary: Picture day reminder");
        prompt.Should().Contain("From: Ms. Smith");
        prompt.Should().Contain("Student: Kid One");
        prompt.Should().Contain("Sent: 2026-03-02 (Monday, March 2, 2026)");

        prompt.Should().Contain("Picture day is on Friday, March 13.");
        prompt.Should().Contain("- 2026-03-06 (9:00 AM) Picture Day");
        prompt.Should().Contain("Week of 2026-02-23: book fair announcement");

        // The calendar is what turns "next Friday" into a checkable absolute date.
        prompt.Should().Contain("- 2026-03-06 = Friday, March 6, 2026");
        prompt.Should().Contain("- 2026-03-13 = Friday, March 13, 2026");
    }

    [Fact]
    public async Task CritiqueAsync_PromptRendersNoneForAbsentEventsAndLedger()
    {
        RespondWith("""{"findings": []}""");

        await CreateCritic().CritiqueAsync(new SummaryCritiqueRequest([CreateNewsItem()], Draft));

        var prompt = _capturedRequest!.Prompt;

        prompt.Should().MatchRegex(@"ACTIVE EVENTS:\s*None");
        prompt.Should().MatchRegex(@"COVERAGE LEDGER[^\r\n]*:\s*None");
        prompt.Should().NotContain("{{ACTIVE_EVENTS}}");
        prompt.Should().NotContain("{{COVERAGE_LEDGER}}");
        prompt.Should().NotContain("{{SOURCE_ITEMS}}");
        prompt.Should().NotContain("{{DRAFT}}");
        prompt.Should().NotContain("{{DATE_REFERENCE}}");
    }

    [Fact]
    public async Task CritiqueAsync_PromptStampsSourceItemsWithTheirLocalSendDate()
    {
        RespondWith("""{"findings": []}""");

        // 19:30 on March 1 in Los Angeles is already March 2 in UTC. A critic told the item was
        // sent on March 2 would resolve every relative phrase in it a day late and then report a
        // correct draft as wrong.
        var newsItem = CreateNewsItem(sentAt: new DateTime(2026, 3, 2, 3, 30, 0, DateTimeKind.Utc));

        await CreateCritic("America/Los_Angeles").CritiqueAsync(
            new SummaryCritiqueRequest([newsItem], Draft));

        var prompt = _capturedRequest!.Prompt;

        prompt.Should().Contain("Sent: 2026-03-01 (Sunday, March 1, 2026)");
        prompt.Should().NotContain("Sent: 2026-03-02");
    }

    [Fact]
    public async Task CritiqueAsync_AncientSourceItemDoesNotPushTodayOutOfTheCalendar()
    {
        RespondWith("""{"findings": []}""");

        var stale = CreateNewsItem(sentAt: new DateTime(2019, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        await CreateCritic().CritiqueAsync(new SummaryCritiqueRequest([stale], Draft));

        var prompt = _capturedRequest!.Prompt;

        // The window is clamped around today rather than stretched back to 2019, so the dates the
        // draft actually talks about stay in the calendar.
        prompt.Should().Contain("- 2026-03-09 = Monday, March 9, 2026");
        prompt.Should().Contain("- 2026-03-13 = Friday, March 13, 2026");
        prompt.Should().NotContain("= Wednesday, May 1, 2019");
    }

    [Fact]
    public async Task CritiqueAsync_SendsTheCritiqueProfileIncludingItsReasoningSettings()
    {
        RespondWith("""{"findings": []}""");

        await CreateCritic().CritiqueAsync(CreateRequest());

        // The Claude 5 family takes adaptive thinking plus an effort level and rejects a fixed
        // budget. Dropping these would run the critic with thinking off, which is precisely the
        // mode that cannot do date arithmetic, while still paying the raised token ceiling.
        _capturedRequest.Should().NotBeNull();
        _capturedRequest!.ModelId.Should().Be("claude-sonnet-5");
        _capturedRequest.MaxTokens.Should().Be(8192);
        _capturedRequest.Thinking.Should().Be(AiThinkingModes.Adaptive);
        _capturedRequest.Effort.Should().Be(AiEffortLevels.High);
    }

    private static SummaryCritiqueRequest CreateRequest()
        => new([CreateNewsItem()], Draft);

    private static NewsItem CreateNewsItem(
        string newsContent = "Picture day is next Friday.",
        string aiSummary = "Picture day",
        string fromName = "Ms. Smith",
        string studentName = "Kid One",
        DateTime? sentAt = null)
        => new()
        {
            Id = 1,
            ParentId = 1,
            SourceMessageId = "msg-001",
            SourceType = SourceType.MessageText,
            NewsContent = newsContent,
            AiSummary = aiSummary,
            FromName = fromName,
            StudentName = studentName,
            SentAt = sentAt ?? new DateTime(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc)
        };

    private SummaryCritic CreateCritic(string timeZone = "UTC")
        => new(
            _mockAiClient.Object,
            Options.Create(new AiOptions
            {
                Provider = "Anthropic",
                Profiles = new AiProfilesOptions
                {
                    // Deliberately different from the critique profile: a critic that reached for
                    // the summarization profile would still look configured and would silently
                    // review with the wrong model and the wrong ceiling.
                    Summarization = new AiProfileOptions
                    {
                        ModelId = "claude-opus-5",
                        MaxTokens = 32000,
                        Thinking = AiThinkingModes.Adaptive,
                        Effort = AiEffortLevels.Max
                    },
                    Critique = new AiProfileOptions
                    {
                        ModelId = "claude-sonnet-5",
                        MaxTokens = 8192,
                        Thinking = AiThinkingModes.Adaptive,
                        Effort = AiEffortLevels.High
                    }
                }
            }),
            Options.Create(new PipelineScheduleOptions { TimeZone = timeZone }),
            NullLogger<SummaryCritic>.Instance,
            _timeProvider);

    private void RespondWith(string responseText, string? stopReason = null)
    {
        _mockAiClient
            .Setup(client => client.CompleteAsync(It.IsAny<AiCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AiCompletionRequest, CancellationToken>((request, _) => _capturedRequest = request)
            .ReturnsAsync(new AiCompletionResult(responseText, null, stopReason));
    }
}
