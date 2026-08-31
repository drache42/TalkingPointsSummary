using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class SummaryGeneratorTests : IDisposable
{
    /// <summary>
    /// A Monday. The schedule timezone defaults to UTC, so this is also the local date the
    /// generator works from, which makes the rendered weekdays checkable by hand.
    /// </summary>
    private static readonly DateTimeOffset FixedNow = new(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db;
    private readonly Mock<IAiClient> _mockAiClient;
    private readonly AiOptions _aiOptions;

    public SummaryGeneratorTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _mockAiClient = new Mock<IAiClient>();
        _aiOptions = new AiOptions
        {
            Provider = "Anthropic",
            Profiles = new AiProfilesOptions
            {
                Summarization = new AiProfileOptions
                {
                    ModelId = "claude-sonnet-4-5-20250929",
                    MaxTokens = 8192,
                    Thinking = AiThinkingModes.Adaptive,
                    Effort = AiEffortLevels.High
                }
            }
        };
    }

    private SummaryGenerator CreateGenerator(TimeProvider? timeProvider = null)
        => new(
            _mockAiClient.Object,
            _db,
            Options.Create(_aiOptions),
            Options.Create(new PipelineScheduleOptions()),
            NullLogger<SummaryGenerator>.Instance,
            new GradeCalculator(),
            timeProvider);

    private SummaryGenerator CreateGeneratorAtFixedNow()
        => CreateGenerator(new FixedTimeProvider(FixedNow));

    [Fact]
    public async Task BuildPromptAsync_NoNewsItems_ReturnsNull()
    {
        var parent = await SeedParentAsync();

        var generator = CreateGenerator();
        var result = await generator.BuildPromptAsync(parent);

        result.Should().BeNull();
    }

    [Fact]
    public async Task BuildPromptAsync_WithNewsItems_ReturnsPromptResult()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id);
        await SeedNewsItemAsync(parent.Id);

        var generator = CreateGenerator();
        var result = await generator.BuildPromptAsync(parent);

        result.Should().NotBeNull();
        result!.Prompt.Should().NotBeNullOrEmpty();
        result.NewsItemCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildPromptAsync_WithMultipleNewsItems_ReflectsCount()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id);
        await SeedNewsItemAsync(parent.Id);
        await SeedNewsItemAsync(parent.Id);
        await SeedNewsItemAsync(parent.Id);

        var generator = CreateGenerator();
        var result = await generator.BuildPromptAsync(parent);

        result!.NewsItemCount.Should().Be(3);
    }

    /// <summary>
    /// Eligibility is the recorded fact that an item has not been fed into a digest yet. An item
    /// carrying an IncludedInSummaryId was already reported and must not come back.
    /// </summary>
    [Fact]
    public async Task BuildPromptAsync_SkipsNewsItemsAlreadyFedIntoADigest()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id);

        var priorSummary = await SeedSummaryAsync(parent.Id, FixedNow.UtcDateTime.AddDays(-7), "### Older topic\nBody.");

        await SeedNewsItemAsync(parent.Id, content: "Brand new classroom note");
        await SeedNewsItemAsync(parent.Id, content: "Second brand new note");
        await SeedNewsItemAsync(parent.Id, content: "Already reported note", includedInSummaryId: priorSummary.Id);

        var result = await CreateGeneratorAtFixedNow().BuildPromptAsync(parent);

        result!.NewsItemCount.Should().Be(2);
        result.Prompt.Should().Contain("Brand new classroom note");
        result.Prompt.Should().Contain("Second brand new note");
        result.Prompt.Should().NotContain("Already reported note");
    }

    [Fact]
    public async Task BuildPromptAsync_EveryNewsItemAlreadyReported_ReturnsNull()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id);

        var priorSummary = await SeedSummaryAsync(parent.Id, FixedNow.UtcDateTime.AddDays(-7), "### Older topic\nBody.");
        await SeedNewsItemAsync(parent.Id, content: "Already reported", includedInSummaryId: priorSummary.Id);

        var result = await CreateGeneratorAtFixedNow().BuildPromptAsync(parent);

        result.Should().BeNull();
    }

    /// <summary>
    /// The old six-week window silently dropped anything older, so a message that arrived late or
    /// was analyzed late was never reported at all. There is no window any more.
    /// </summary>
    [Fact]
    public async Task BuildPromptAsync_UnreportedItemFarOlderThanTheOldWindow_IsStillIncluded()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id);

        var longAgo = FixedNow.UtcDateTime.AddDays(-300);
        await SeedNewsItemAsync(parent.Id, sentAt: longAgo, createdAt: longAgo, content: "Stale but never reported");

        var result = await CreateGeneratorAtFixedNow().BuildPromptAsync(parent);

        result.Should().NotBeNull();
        result!.NewsItemCount.Should().Be(1);
        result.Prompt.Should().Contain("Stale but never reported");
    }

    [Fact]
    public async Task BuildPromptAsync_RendersActiveFutureEventsGroupedBySchool()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id, name: "StudentOne", school: "Sample Elementary");
        await SeedChildAsync(parent.Id, name: "StudentTwo", school: "Demo Elementary");
        var newsItem = await SeedNewsItemAsync(parent.Id);

        await SeedTrackedEventAsync(parent.Id, newsItem.Id, "Sample Elementary", new DateTime(2026, 3, 12), "Field Day", timeText: "9:00 AM");
        await SeedTrackedEventAsync(parent.Id, newsItem.Id, "Sample Elementary", new DateTime(2026, 3, 20), "Book Fair");
        await SeedTrackedEventAsync(parent.Id, newsItem.Id, "Demo Elementary", new DateTime(2026, 3, 18), "Picture Day");

        var result = await CreateGeneratorAtFixedNow().BuildPromptAsync(parent);

        result!.Prompt.Should().Contain("### Sample Elementary (StudentOne)");
        result.Prompt.Should().Contain("### Demo Elementary (StudentTwo)");
        result.Prompt.Should().Contain("- **Thursday, March 12, 2026** - Field Day (9:00 AM)");
        result.Prompt.Should().Contain("- **Friday, March 20, 2026** - Book Fair");
        result.Prompt.Should().Contain("- **Wednesday, March 18, 2026** - Picture Day");
    }

    /// <summary>
    /// The dates query deliberately ignores IncludedInSummaryId. An event announced in a message
    /// that was written up weeks ago still has to be listed until the day it happens.
    /// </summary>
    [Fact]
    public async Task BuildPromptAsync_EventWhoseSourceItemWasAlreadyReported_IsStillListed()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id, name: "StudentOne", school: "Sample Elementary");

        var priorSummary = await SeedSummaryAsync(parent.Id, FixedNow.UtcDateTime.AddDays(-14), "### Older topic\nBody.");
        var reportedItem = await SeedNewsItemAsync(
            parent.Id, content: "Announced weeks ago", includedInSummaryId: priorSummary.Id);

        // Something unreported has to exist or there would be no digest at all this week.
        await SeedNewsItemAsync(parent.Id, content: "This week's note");

        await SeedTrackedEventAsync(
            parent.Id, reportedItem.Id, "Sample Elementary", new DateTime(2026, 4, 24), "Spring Concert", timeText: "6:30 PM");

        var result = await CreateGeneratorAtFixedNow().BuildPromptAsync(parent);

        result!.Prompt.Should().NotContain("Announced weeks ago");
        result.Prompt.Should().Contain("Spring Concert");
        result.Prompt.Should().Contain("April 24, 2026");
    }

    /// <summary>
    /// There is no forward cutoff: an event a year out is still an event a parent should see.
    /// </summary>
    [Fact]
    public async Task BuildPromptAsync_EventFarInTheFuture_IsStillListed()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id, name: "StudentOne", school: "Sample Elementary");
        var newsItem = await SeedNewsItemAsync(parent.Id);

        await SeedTrackedEventAsync(
            parent.Id, newsItem.Id, "Sample Elementary", new DateTime(2027, 4, 15), "Fifth Grade Promotion");

        var result = await CreateGeneratorAtFixedNow().BuildPromptAsync(parent);

        result!.Prompt.Should().Contain("Fifth Grade Promotion");
        result.Prompt.Should().Contain("April 15, 2027");
    }

    [Fact]
    public async Task BuildPromptAsync_SupersededOrCancelledEvents_AreNotListed()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id, name: "StudentOne", school: "Sample Elementary");
        var newsItem = await SeedNewsItemAsync(parent.Id);

        await SeedTrackedEventAsync(
            parent.Id, newsItem.Id, "Sample Elementary", new DateTime(2026, 3, 12), "Moved Assembly",
            status: TrackedEventStatus.Superseded);
        await SeedTrackedEventAsync(
            parent.Id, newsItem.Id, "Sample Elementary", new DateTime(2026, 3, 13), "Called Off Assembly",
            status: TrackedEventStatus.Cancelled);
        await SeedTrackedEventAsync(
            parent.Id, newsItem.Id, "Sample Elementary", new DateTime(2026, 3, 14), "Still On Assembly");

        var result = await CreateGeneratorAtFixedNow().BuildPromptAsync(parent);

        result!.Prompt.Should().Contain("Still On Assembly");
        result.Prompt.Should().NotContain("Moved Assembly");
        result.Prompt.Should().NotContain("Called Off Assembly");
    }

    [Fact]
    public async Task BuildPromptAsync_EventThatAlreadyHappened_IsNotListed()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id, name: "StudentOne", school: "Sample Elementary");
        var newsItem = await SeedNewsItemAsync(parent.Id);

        await SeedTrackedEventAsync(
            parent.Id, newsItem.Id, "Sample Elementary", new DateTime(2026, 3, 8), "Yesterday Assembly");
        await SeedTrackedEventAsync(
            parent.Id, newsItem.Id, "Sample Elementary", new DateTime(2026, 3, 9), "Today Assembly");

        var result = await CreateGeneratorAtFixedNow().BuildPromptAsync(parent);

        result!.Prompt.Should().NotContain("Yesterday Assembly");
        result.Prompt.Should().Contain("Today Assembly");
    }

    /// <summary>
    /// Prior digests are no longer pasted in whole. Only the newest is quoted; the rest survive
    /// as dated index lines so the model can tell them apart by age.
    /// </summary>
    [Fact]
    public async Task BuildPromptAsync_IndexesOlderDigestsAndQuotesOnlyTheNewest()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id);
        await SeedNewsItemAsync(parent.Id);

        await SeedSummaryAsync(
            parent.Id,
            new DateTime(2026, 3, 2, 13, 0, 0, DateTimeKind.Utc),
            "### Spring Concert\nTickets go on sale Monday.");
        await SeedSummaryAsync(
            parent.Id,
            new DateTime(2026, 2, 2, 13, 0, 0, DateTimeKind.Utc),
            "### Book Fair\nVolunteers are still needed at the register.");

        var result = await CreateGeneratorAtFixedNow().BuildPromptAsync(parent);

        result!.Prompt.Should().Contain("2026-03-02 digest:");
        result.Prompt.Should().Contain("2026-02-02 digest:");
        result.Prompt.Should().Contain("Sent 2026-03-02:");
        result.Prompt.Should().Contain("Tickets go on sale Monday.");

        // The older digest contributes its topic to the index and nothing more.
        result.Prompt.Should().Contain("Book Fair");
        result.Prompt.Should().NotContain("Volunteers are still needed");
    }

    [Fact]
    public async Task ExecutePromptAsync_SendsTheWholeSummarizationProfileIncludingItsReasoningSettings()
    {
        var captured = CaptureRequest("# Summary\nContent");

        var generator = CreateGenerator();
        var result = await generator.ExecutePromptAsync("the prompt");

        captured.Value.Should().NotBeNull();
        captured.Value!.Prompt.Should().Be("the prompt");
        captured.Value.ModelId.Should().Be("claude-sonnet-4-5-20250929");
        captured.Value.MaxTokens.Should().Be(8192);

        // Reasoning settings that never leave the generator mean every digest is produced with
        // thinking off, silently, while still paying for the token ceiling raised to hold thinking
        // tokens. Nothing else in the pipeline would notice.
        captured.Value.Thinking.Should().Be(AiThinkingModes.Adaptive);
        captured.Value.Effort.Should().Be(AiEffortLevels.High);

        result.Should().Be("# Summary\nContent");
    }

    /// <summary>
    /// The standing instructions are identical on every run, so they travel as the system prompt
    /// while the user message carries only this week's content.
    /// </summary>
    [Fact]
    public async Task ExecutePromptAsync_SendsTheStandingInstructionsAsASystemPrompt()
    {
        var captured = CaptureRequest("# Summary\nContent");

        await CreateGenerator().ExecutePromptAsync("the prompt");

        captured.Value!.Prompt.Should().Be("the prompt");
        captured.Value.SystemPrompt.Should().NotBeNullOrEmpty();
        captured.Value.SystemPrompt.Should().Contain("never been fed into a digest");
        captured.Value.SystemPrompt.Should().NotContain("{{");
    }

    [Fact]
    public async Task ExecutePromptAsync_BudgetThinkingProfile_SendsTheConfiguredBudget()
    {
        _aiOptions.Profiles.Summarization = new AiProfileOptions
        {
            ModelId = "claude-haiku-4-5-20251001",
            MaxTokens = 8192,
            Thinking = AiThinkingModes.Budget,
            ThinkingBudgetTokens = 4096
        };

        var captured = CaptureRequest("# Summary\nContent");

        await CreateGenerator().ExecutePromptAsync("the prompt");

        captured.Value!.Thinking.Should().Be(AiThinkingModes.Budget);
        captured.Value.ThinkingBudgetTokens.Should().Be(4096);
        captured.Value.Effort.Should().BeNull();
    }

    /// <summary>
    /// Records the request the generator actually builds, so assertions are made against the
    /// request itself rather than against the mock having been called.
    /// </summary>
    private CapturedRequest CaptureRequest(string responseText)
    {
        var captured = new CapturedRequest();
        _mockAiClient
            .Setup(c => c.CompleteAsync(It.IsAny<AiCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AiCompletionRequest, CancellationToken>((request, _) => captured.Value = request)
            .ReturnsAsync(new AiCompletionResult(responseText));
        return captured;
    }

    private sealed class CapturedRequest
    {
        public AiCompletionRequest? Value { get; set; }
    }

    [Fact]
    public async Task ExecutePromptAsync_TruncatedResponse_ThrowsRatherThanReturningAPartialDigest()
    {
        // The client returns the partial text so a categorization can fall back on it. A digest
        // cannot: the cut-off tail would be converted to HTML and emailed.
        _mockAiClient
            .Setup(c => c.CompleteAsync(It.IsAny<AiCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiCompletionResult(
                "# Weekly digest\nThis was cut off mid-", null, "max_tokens"));

        var generator = CreateGenerator();

        var act = async () => await generator.ExecutePromptAsync("prompt");

        var assertion = await act.Should().ThrowAsync<AiResponseTruncatedException>();
        assertion.Which.Message.Should().Contain("claude-sonnet-4-5-20250929");
        assertion.Which.Message.Should().Contain("8192");
    }

    [Fact]
    public async Task ExecutePromptAsync_NonTruncatingStopReason_ReturnsTheMarkdown()
    {
        _mockAiClient
            .Setup(c => c.CompleteAsync(It.IsAny<AiCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiCompletionResult("# Weekly digest\nAll of it.", null, "end_turn"));

        var generator = CreateGenerator();
        var result = await generator.ExecutePromptAsync("prompt");

        result.Should().Be("# Weekly digest\nAll of it.");
    }

    [Fact]
    public async Task ExecutePromptAsync_EmptyAiResponse_ReturnsNull()
    {
        _mockAiClient
            .Setup(c => c.CompleteAsync(It.IsAny<AiCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiCompletionResult(string.Empty));

        var generator = CreateGenerator();
        var result = await generator.ExecutePromptAsync("prompt");

        result.Should().BeNull();
    }

    [Fact]
    public async Task BuildPromptAsync_ExcludesSummariesWithNullContent()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id);
        await SeedNewsItemAsync(parent.Id);

        // An incomplete summary: the prompt row was saved, generation never produced content.
        _db.Summaries.Add(new Summary
        {
            ParentId = parent.Id,
            Prompt = "old prompt",
            Content = null,
            CreatedAt = FixedNow.UtcDateTime.AddDays(-1)
        });
        await _db.SaveChangesAsync();

        var result = await CreateGeneratorAtFixedNow().BuildPromptAsync(parent);

        // With no completed digest on file, both history tokens fall back to "None" rather than
        // indexing or quoting a digest that was never sent.
        result.Should().NotBeNull();
        result!.Prompt.Should().NotContain("2026-03-08 digest:");
        result.Prompt.Should().NotContain("Sent 2026-");
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Parent> SeedParentAsync()
    {
        var parent = new Parent
        {
            Name = "Test Parent",
            TalkingPointsToken = "token",
            TalkingPointsContactId = "contact",
            EmailRecipients = "test@example.com",
            IsActive = true
        };
        _db.Parents.Add(parent);
        await _db.SaveChangesAsync();
        return parent;
    }

    private async Task<Child> SeedChildAsync(
        int parentId,
        string name = "Test Child",
        string school = "Test School")
    {
        var child = new Child
        {
            ParentId = parentId,
            Name = name,
            School = school,
            StartingGrade = 5,
            StartingYear = 2020,
            Emoji = "\U0001F4DA"
        };
        _db.Children.Add(child);
        await _db.SaveChangesAsync();
        return child;
    }

    private async Task<NewsItem> SeedNewsItemAsync(
        int parentId,
        DateTime? sentAt = null,
        DateTime? createdAt = null,
        string content = "School news content",
        int? includedInSummaryId = null)
    {
        var item = new NewsItem
        {
            ParentId = parentId,
            SourceMessageId = Guid.NewGuid().ToString(),
            SourceType = SourceType.MessageText,
            NewsContent = content,
            AiSummary = "AI summary",
            FromName = "Teacher",
            StudentName = "Student",
            SentAt = sentAt ?? FixedNow.UtcDateTime.AddDays(-2),
            AnalyzedAt = FixedNow.UtcDateTime,
            CreatedAt = createdAt ?? FixedNow.UtcDateTime,
            IncludedInSummaryId = includedInSummaryId
        };
        _db.NewsItems.Add(item);
        await _db.SaveChangesAsync();
        return item;
    }

    private async Task<Summary> SeedSummaryAsync(int parentId, DateTime createdAt, string content)
    {
        var summary = new Summary
        {
            ParentId = parentId,
            Prompt = "prompt",
            Content = content,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)
        };
        _db.Summaries.Add(summary);
        await _db.SaveChangesAsync();
        return summary;
    }

    private async Task<TrackedEvent> SeedTrackedEventAsync(
        int parentId,
        int sourceNewsItemId,
        string school,
        DateTime eventDate,
        string title,
        string? timeText = null,
        TrackedEventStatus status = TrackedEventStatus.Active)
    {
        var trackedEvent = new TrackedEvent
        {
            ParentId = parentId,
            SourceNewsItemId = sourceNewsItemId,
            School = school,
            EventDate = DateTime.SpecifyKind(eventDate.Date, DateTimeKind.Utc),
            Title = title,
            TimeText = timeText,
            Status = status,
            CreatedAt = FixedNow.UtcDateTime
        };
        _db.TrackedEvents.Add(trackedEvent);
        await _db.SaveChangesAsync();
        return trackedEvent;
    }
}
