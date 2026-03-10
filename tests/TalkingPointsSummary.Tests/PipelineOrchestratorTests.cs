using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class PipelineOrchestratorTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Mock<ITalkingPointsApiClient> _mockApiClient;
    private readonly Mock<IMessageDeduplicator> _mockDeduplicator;
    private readonly Mock<IMessageCategorizer> _mockCategorizer;
    private readonly Mock<INewsletterScraper> _mockScraper;
    private readonly Mock<ISummaryGenerator> _mockSummaryGenerator;
    private readonly Mock<IMarkdownConverter> _mockMarkdownConverter;
    private readonly Mock<IEmailSender> _mockEmailSender;
    private readonly PipelineOrchestrator _orchestrator;
    private readonly Parent _testParent;

    public PipelineOrchestratorTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new AppDbContext(options);

        _testParent = new Parent
        {
            Name = "Test Parent",
            TalkingPointsToken = "test-token",
            TalkingPointsContactId = "test-contact",
            EmailRecipients = "test@example.com"
        };
        _db.Parents.Add(_testParent);
        _db.Children.Add(new Child
        {
            ParentId = 1,
            Name = "Test Child",
            School = "Test School",
            StartingGrade = 0,
            StartingYear = 2025,
            Emoji = "📚"
        });
        _db.SaveChanges();

        _mockApiClient = new Mock<ITalkingPointsApiClient>();
        _mockDeduplicator = new Mock<IMessageDeduplicator>();
        _mockCategorizer = new Mock<IMessageCategorizer>();
        _mockScraper = new Mock<INewsletterScraper>();
        _mockSummaryGenerator = new Mock<ISummaryGenerator>();
        _mockMarkdownConverter = new Mock<IMarkdownConverter>();
        _mockEmailSender = new Mock<IEmailSender>();

        // Default setup: API returns empty, dedup returns empty
        _mockApiClient.Setup(x => x.FetchMessagesAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockDeduplicator.Setup(x => x.DeduplicateAndSaveAsync(It.IsAny<Parent>(), It.IsAny<List<TalkingPointsMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _orchestrator = new PipelineOrchestrator(
            _db,
            _mockApiClient.Object,
            _mockDeduplicator.Object,
            _mockCategorizer.Object,
            _mockScraper.Object,
            _mockSummaryGenerator.Object,
            _mockMarkdownConverter.Object,
            _mockEmailSender.Object,
            NullLogger<PipelineOrchestrator>.Instance);
    }

    [Fact]
    public async Task RunAsync_SummaryGeneratorReturnsNull_ReturnsEarlyWithoutEmailOrSave()
    {
        _mockSummaryGenerator.Setup(x => x.GenerateAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await _orchestrator.RunAsync(_testParent);

        _mockEmailSender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _db.Summaries.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ProcessMessageThrows_ContinuesProcessingRemainingMessages()
    {
        var messages = await SeedUnprocessedMessagesAsync(
            new Message { ExternalMessageId = "msg-fail", FromName = "Teacher A", MessageText = "Fail", SentAt = DateTime.UtcNow },
            new Message { ExternalMessageId = "msg-ok", FromName = "Teacher B", MessageText = "OK", StudentName = "Clara", SentAt = DateTime.UtcNow });

        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(messages);

        // First message categorization throws
        _mockCategorizer.Setup(x => x.CategorizeAsync(It.Is<Message>(m => m.ExternalMessageId == "msg-fail"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API error"));

        // Second message categorization succeeds with IsNewsItself
        _mockCategorizer.Setup(x => x.CategorizeAsync(It.Is<Message>(m => m.ExternalMessageId == "msg-ok"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategorizationResult { MessageId = "msg-ok", IsNewsItself = true, Summary = "News" });

        _mockSummaryGenerator.Setup(x => x.GenerateAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await _orchestrator.RunAsync(_testParent);

        // Second message should still be categorized and processed
        _mockCategorizer.Verify(x => x.CategorizeAsync(It.Is<Message>(m => m.ExternalMessageId == "msg-ok"), It.IsAny<CancellationToken>()), Times.Once);

        var processedMessage = await _db.Messages.SingleAsync(m => m.ExternalMessageId == "msg-ok");
        processedMessage.ProcessedAt.Should().NotBeNull();

        var failedMessage = await _db.Messages.SingleAsync(m => m.ExternalMessageId == "msg-fail");
        failedMessage.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_HasNewsletterUrl_ScraperReturnsText_SavesNewsletterUrlSourceType()
    {
        var message = await SeedUnprocessedMessageAsync(new Message
        {
            ExternalMessageId = "msg-001",
            FromName = "Teacher",
            StudentName = "Clara",
            MessageText = "Check newsletter",
            SentAt = new DateTime(2026, 3, 7, 10, 0, 0, DateTimeKind.Utc)
        });

        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([message]);

        _mockCategorizer.Setup(x => x.CategorizeAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategorizationResult
            {
                MessageId = "msg-001",
                HasNewsletterUrl = true,
                NewsletterUrl = "https://www.smore.com/abc",
                IsNewsItself = false,
                Summary = "Newsletter link"
            });

        _mockScraper.Setup(x => x.ScrapeAsync("https://www.smore.com/abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Scraped newsletter content");

        _mockSummaryGenerator.Setup(x => x.GenerateAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await _orchestrator.RunAsync(_testParent);

        var savedItem = await _db.NewsItems.SingleAsync();
        savedItem.SourceType.Should().Be(SourceType.NewsletterUrl);
        savedItem.NewsContent.Should().Be("Scraped newsletter content");
        savedItem.NewsletterUrl.Should().Be("https://www.smore.com/abc");
        savedItem.StudentName.Should().Be("Clara");
        savedItem.FromName.Should().Be("Teacher");
    }

    [Fact]
    public async Task RunAsync_HasNewsletterUrl_ScraperReturnsNull_FallsBackToMessageText()
    {
        var message = await SeedUnprocessedMessageAsync(new Message
        {
            ExternalMessageId = "msg-001",
            FromName = "Teacher",
            StudentName = "Clara",
            MessageText = "Original message text",
            SentAt = DateTime.UtcNow
        });

        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([message]);

        _mockCategorizer.Setup(x => x.CategorizeAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategorizationResult
            {
                MessageId = "msg-001",
                HasNewsletterUrl = true,
                NewsletterUrl = "https://www.smore.com/abc",
                IsNewsItself = false,
                Summary = "Newsletter link"
            });

        _mockScraper.Setup(x => x.ScrapeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        _mockSummaryGenerator.Setup(x => x.GenerateAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await _orchestrator.RunAsync(_testParent);

        var savedItem = await _db.NewsItems.SingleAsync();
        savedItem.SourceType.Should().Be(SourceType.MessageText);
        savedItem.NewsContent.Should().Be("Original message text");
    }

    [Fact]
    public async Task RunAsync_IsNewsItself_SavesMessageTextSourceType()
    {
        var message = await SeedUnprocessedMessageAsync(new Message
        {
            ExternalMessageId = "msg-001",
            FromName = "Teacher",
            StudentName = "Clara",
            MessageText = "Picture day is Friday",
            SentAt = DateTime.UtcNow
        });

        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([message]);

        _mockCategorizer.Setup(x => x.CategorizeAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategorizationResult
            {
                MessageId = "msg-001",
                HasNewsletterUrl = false,
                IsNewsItself = true,
                Summary = "Picture day"
            });

        _mockSummaryGenerator.Setup(x => x.GenerateAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await _orchestrator.RunAsync(_testParent);

        var savedItem = await _db.NewsItems.SingleAsync();
        savedItem.SourceType.Should().Be(SourceType.MessageText);
        savedItem.NewsContent.Should().Be("Picture day is Friday");
    }

    [Fact]
    public async Task RunAsync_MessageHasNewsAndNewsletterLink_SavesBothSourceTypesOnce()
    {
        var message = await SeedUnprocessedMessageAsync(new Message
        {
            ExternalMessageId = "msg-both",
            FromName = "Teacher",
            StudentName = "Clara",
            MessageText = "Picture day is Friday. Full details in the newsletter.",
            SentAt = DateTime.UtcNow
        });

        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([message]);

        _mockCategorizer.Setup(x => x.CategorizeAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategorizationResult
            {
                MessageId = "msg-both",
                HasNewsletterUrl = true,
                NewsletterUrl = "https://www.smore.com/abc",
                IsNewsItself = true,
                Summary = "Picture day and newsletter"
            });

        _mockScraper.Setup(x => x.ScrapeAsync("https://www.smore.com/abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Newsletter details here");

        _mockSummaryGenerator.Setup(x => x.GenerateAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await _orchestrator.RunAsync(_testParent);

        var savedItems = await _db.NewsItems
            .Where(newsItem => newsItem.SourceMessageId == "msg-both")
            .OrderBy(newsItem => newsItem.SourceType)
            .ToListAsync();

        savedItems.Should().HaveCount(2);
        savedItems.Should().ContainSingle(newsItem => newsItem.SourceType == SourceType.MessageText)
            .Which.NewsContent.Should().Be("Picture day is Friday. Full details in the newsletter.");
        savedItems.Should().ContainSingle(newsItem => newsItem.SourceType == SourceType.NewsletterUrl)
            .Which.NewsContent.Should().Be("Newsletter details here");
    }

    [Fact]
    public async Task RunAsync_HappyPath_SavesSummaryAndSendsEmail()
    {
        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _mockSummaryGenerator.Setup(x => x.GenerateAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Weekly Summary\nContent here");

        _mockMarkdownConverter.Setup(x => x.ToHtml(It.IsAny<string>()))
            .Returns("<h1>Weekly Summary</h1><p>Content here</p>");

        await _orchestrator.RunAsync(_testParent);

        _mockEmailSender.Verify(x => x.SendAsync(
            "test@example.com",
            "Talking Points Summary V2",
            "<h1>Weekly Summary</h1><p>Content here</p>",
            It.IsAny<CancellationToken>()), Times.Once);

        var savedSummary = await _db.Summaries.SingleAsync();
        savedSummary.Content.Should().Be("# Weekly Summary\nContent here");
        savedSummary.ParentId.Should().Be(_testParent.Id);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private async Task<Message> SeedUnprocessedMessageAsync(Message message)
    {
        message.ParentId = _testParent.Id;
        _db.Messages.Add(message);
        await _db.SaveChangesAsync();
        return message;
    }

    private async Task<List<Message>> SeedUnprocessedMessagesAsync(params Message[] messages)
    {
        foreach (var message in messages)
        {
            message.ParentId = _testParent.Id;
        }

        _db.Messages.AddRange(messages);
        await _db.SaveChangesAsync();
        return messages.ToList();
    }
}
