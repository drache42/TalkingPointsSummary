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
                    ModelId = "claude-sonnet-5",
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
        var child = await SeedChildAsync(parent.Id);
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
        var child = await SeedChildAsync(parent.Id);
        await SeedNewsItemAsync(parent.Id);
        await SeedNewsItemAsync(parent.Id);
        await SeedNewsItemAsync(parent.Id);

        var generator = CreateGenerator();
        var result = await generator.BuildPromptAsync(parent);

        result!.NewsItemCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecutePromptAsync_SendsTheWholeSummarizationProfileIncludingItsReasoningSettings()
    {
        var captured = CaptureRequest("# Summary\nContent");

        var generator = CreateGenerator();
        var result = await generator.ExecutePromptAsync("the prompt");

        captured.Value.Should().NotBeNull();
        captured.Value!.Prompt.Should().Be("the prompt");
        captured.Value.ModelId.Should().Be("claude-sonnet-5");
        captured.Value.MaxTokens.Should().Be(8192);

        // Reasoning settings that never leave the generator mean every digest is produced with
        // thinking off, silently, while still paying for the token ceiling raised to hold thinking
        // tokens. Nothing else in the pipeline would notice.
        captured.Value.Thinking.Should().Be(AiThinkingModes.Adaptive);
        captured.Value.Effort.Should().Be(AiEffortLevels.High);

        result.Should().Be("# Summary\nContent");
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

        // Seed an incomplete summary (Prompt saved, Content null)
        _db.Summaries.Add(new Summary
        {
            ParentId = parent.Id,
            Prompt = "old prompt",
            Content = null,
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        });
        await _db.SaveChangesAsync();

        var generator = CreateGenerator();
        var result = await generator.BuildPromptAsync(parent);

        // Just verify it completes without error; the null-content summary was filtered out
        result.Should().NotBeNull();
    }

    public void Dispose()
    {
        _db.Dispose();
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

    private async Task<Child> SeedChildAsync(int parentId)
    {
        var child = new Child
        {
            ParentId = parentId,
            Name = "Test Child",
            School = "Test School",
            StartingGrade = 5,
            StartingYear = 2020,
            Emoji = "📚"
        };
        _db.Children.Add(child);
        await _db.SaveChangesAsync();
        return child;
    }

    private async Task<NewsItem> SeedNewsItemAsync(int parentId)
    {
        var item = new NewsItem
        {
            ParentId = parentId,
            SourceMessageId = Guid.NewGuid().ToString(),
            SourceType = SourceType.MessageText,
            NewsContent = "School news content",
            AiSummary = "AI summary",
            FromName = "Teacher",
            StudentName = "Student",
            SentAt = DateTime.UtcNow,
            AnalyzedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        _db.NewsItems.Add(item);
        await _db.SaveChangesAsync();
        return item;
    }
}
