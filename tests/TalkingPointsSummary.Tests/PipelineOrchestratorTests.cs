using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class PipelineOrchestratorTests : IDisposable
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 3, 10, 9, 15, 0, TimeSpan.Zero);

    private readonly AppDbContext _db;
    private readonly Mock<ITalkingPointsApiClient> _mockApiClient;
    private readonly Mock<IMessageDeduplicator> _mockDeduplicator;
    private readonly Mock<IMessageCategorizer> _mockCategorizer;
    private readonly Mock<INewsletterScraper> _mockScraper;
    private readonly Mock<IEventExtractor> _mockEventExtractor;
    private readonly Mock<ISummaryCritic> _mockCritic;
    private readonly Mock<ISummaryGenerator> _mockSummaryGenerator;
    private readonly Mock<IMarkdownConverter> _mockMarkdownConverter;
    private readonly Mock<IEmailSender> _mockEmailSender;
    private readonly PipelineOrchestrator _orchestrator;
    private readonly Parent _testParent;
    private readonly FixedTimeProvider _timeProvider;

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
        _mockEventExtractor = new Mock<IEventExtractor>();
        _mockCritic = new Mock<ISummaryCritic>();
        _mockSummaryGenerator = new Mock<ISummaryGenerator>();
        _mockMarkdownConverter = new Mock<IMarkdownConverter>();
        _mockEmailSender = new Mock<IEmailSender>();
        _timeProvider = new FixedTimeProvider(FixedUtcNow);

        // Default setup: API returns empty, dedup returns empty
        _mockApiClient.Setup(x => x.FetchMessagesAsync(It.IsAny<Parent>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockDeduplicator.Setup(x => x.DeduplicateAndSaveAsync(It.IsAny<Parent>(), It.IsAny<List<TalkingPointsMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockEventExtractor.Setup(x => x.ExtractAsync(It.IsAny<NewsItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockCritic.Setup(x => x.CritiqueAsync(It.IsAny<SummaryCritiqueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _orchestrator = new PipelineOrchestrator(
            _db,
            _mockApiClient.Object,
            _mockDeduplicator.Object,
            _mockCategorizer.Object,
            _mockScraper.Object,
            _mockEventExtractor.Object,
            _mockSummaryGenerator.Object,
            new SummaryOutputValidator(),
            _mockCritic.Object,
            _mockMarkdownConverter.Object,
            _mockEmailSender.Object,
            Options.Create(new PipelineScheduleOptions()),
            NullLogger<PipelineOrchestrator>.Instance,
            _timeProvider);
    }

    [Fact]
    public async Task RunAsync_SummaryGeneratorReturnsNull_ReturnsEarlyWithoutEmailOrSave()
    {
        _mockSummaryGenerator.Setup(x => x.BuildPromptAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SummaryPromptResult?)null);

        await _orchestrator.RunAsync(_testParent);

        _mockEmailSender.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _db.Summaries.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_PassesNewestSavedMessageIdToApiClient()
    {
        _db.Messages.AddRange(
            new Message
            {
                ParentId = _testParent.Id,
                ExternalMessageId = "older-msg",
                FromName = "Teacher",
                MessageText = "Older",
                SentAt = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc),
                CreatedAt = DateTime.UtcNow,
            },
            new Message
            {
                ParentId = _testParent.Id,
                ExternalMessageId = "latest-msg",
                FromName = "Teacher",
                MessageText = "Latest",
                SentAt = new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc),
                CreatedAt = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();

        _mockSummaryGenerator.Setup(x => x.BuildPromptAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SummaryPromptResult?)null);

        await _orchestrator.RunAsync(_testParent);

        _mockApiClient.Verify(
            x => x.FetchMessagesAsync(
                It.Is<Parent>(parent => parent.Id == _testParent.Id),
                "latest-msg",
                new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc),
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ProcessMessageThrows_ContinuesProcessingRemainingMessages()
    {
        var messages = await SeedUnprocessedMessagesAsync(
            new Message { ExternalMessageId = "msg-fail", FromName = "Teacher A", MessageText = "Fail", SentAt = DateTime.UtcNow },
            new Message { ExternalMessageId = "msg-ok", FromName = "Teacher B", MessageText = "OK", StudentName = "StudentOne", SentAt = DateTime.UtcNow });

        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(messages);

        // First message categorization throws
        _mockCategorizer.Setup(x => x.CategorizeAsync(It.Is<Message>(m => m.ExternalMessageId == "msg-fail"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API error"));

        // Second message categorization succeeds with IsNewsItself
        _mockCategorizer.Setup(x => x.CategorizeAsync(It.Is<Message>(m => m.ExternalMessageId == "msg-ok"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategorizationResult { MessageId = "msg-ok", IsNewsItself = true, Summary = "News" });

        _mockSummaryGenerator.Setup(x => x.BuildPromptAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SummaryPromptResult?)null);

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
            StudentName = "StudentOne",
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

        _mockSummaryGenerator.Setup(x => x.BuildPromptAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SummaryPromptResult?)null);

        await _orchestrator.RunAsync(_testParent);

        var savedItem = await _db.NewsItems.SingleAsync();
        savedItem.SourceType.Should().Be(SourceType.NewsletterUrl);
        savedItem.NewsContent.Should().Be("Scraped newsletter content");
        savedItem.NewsletterUrl.Should().Be("https://www.smore.com/abc");
        savedItem.StudentName.Should().Be("StudentOne");
        savedItem.FromName.Should().Be("Teacher");
        savedItem.AnalyzedAt.Should().Be(FixedUtcNow.UtcDateTime);
        savedItem.CreatedAt.Should().Be(FixedUtcNow.UtcDateTime);
    }

    [Fact]
    public async Task RunAsync_HasNewsletterUrl_ScraperReturnsNull_FallsBackToMessageText()
    {
        var message = await SeedUnprocessedMessageAsync(new Message
        {
            ExternalMessageId = "msg-001",
            FromName = "Teacher",
            StudentName = "StudentOne",
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

        _mockSummaryGenerator.Setup(x => x.BuildPromptAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SummaryPromptResult?)null);

        await _orchestrator.RunAsync(_testParent);

        var savedItem = await _db.NewsItems.SingleAsync();
        savedItem.SourceType.Should().Be(SourceType.MessageText);
        savedItem.NewsContent.Should().Be("Original message text");
        savedItem.NewsletterUrl.Should().Be("https://www.smore.com/abc");
    }

    [Fact]
    public async Task RunAsync_IsNewsItself_SavesMessageTextSourceType()
    {
        var message = await SeedUnprocessedMessageAsync(new Message
        {
            ExternalMessageId = "msg-001",
            FromName = "Teacher",
            StudentName = "StudentOne",
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

        _mockSummaryGenerator.Setup(x => x.BuildPromptAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SummaryPromptResult?)null);

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
            StudentName = "StudentOne",
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

        _mockSummaryGenerator.Setup(x => x.BuildPromptAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SummaryPromptResult?)null);

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

        _mockSummaryGenerator.Setup(x => x.BuildPromptAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SummaryPromptResult("the-prompt", []));
        _mockSummaryGenerator.Setup(x => x.ExecutePromptAsync("the-prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Weekly Summary\nContent here");

        _mockMarkdownConverter.Setup(x => x.ToHtml(It.IsAny<string>()))
            .Returns("<h1>Weekly Summary</h1><p>Content here</p>");

        await _orchestrator.RunAsync(_testParent);

        _mockEmailSender.Verify(x => x.SendAsync(
            "test@example.com",
            "Talking Points Summary",
            "<h1>Weekly Summary</h1><p>Content here</p>",
            It.IsAny<CancellationToken>()), Times.Once);

        var savedSummary = await _db.Summaries.SingleAsync();
        savedSummary.Content.Should().Be("# Weekly Summary\nContent here");
        savedSummary.Prompt.Should().Be("the-prompt");
        savedSummary.ParentId.Should().Be(_testParent.Id);
        savedSummary.CreatedAt.Should().Be(FixedUtcNow.UtcDateTime);
    }

    /// <summary>
    /// Nothing else in the system ever writes TrackedEvents. A pipeline that stores a news item
    /// without asking the extractor to read it produces a digest with no upcoming dates at all,
    /// every week, silently.
    /// </summary>
    [Fact]
    public async Task RunAsync_PersistedNewsItem_IsHandedToTheEventExtractorWithItsAssignedId()
    {
        var message = await SeedUnprocessedMessageAsync(new Message
        {
            ExternalMessageId = "msg-events",
            FromName = "Principal",
            MessageText = "Book Fair is on October 14.",
            StudentName = "Test Child",
            SentAt = new DateTime(2026, 3, 9, 8, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow
        });

        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([message]);

        _mockCategorizer.Setup(x => x.CategorizeAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategorizationResult
            {
                IsNewsItself = true,
                Summary = "Book Fair announcement"
            });

        // The stand-in extractor writes the row a real extractor would, so the assertion lands on
        // persisted state rather than on the mock having been called.
        StubEventExtractor("Book Fair", new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));

        await _orchestrator.RunAsync(_testParent);

        var newsItem = await _db.NewsItems.SingleAsync();
        var trackedEvent = await _db.TrackedEvents.SingleAsync();

        trackedEvent.Title.Should().Be("Book Fair");
        trackedEvent.SourceNewsItemId.Should().Be(newsItem.Id);
        trackedEvent.SourceNewsItemId.Should().NotBe(0, "the news item must be committed before extraction so it has an id");
        trackedEvent.ParentId.Should().Be(_testParent.Id);
    }

    /// <summary>
    /// A message reprocessed after its news item was already stored must not run extraction again:
    /// the events it announced are already on record.
    /// </summary>
    [Fact]
    public async Task RunAsync_NewsItemAlreadyStoredForMessage_DoesNotReExtractEvents()
    {
        var message = await SeedUnprocessedMessageAsync(new Message
        {
            ExternalMessageId = "msg-repeat",
            FromName = "Principal",
            MessageText = "Book Fair is on October 14.",
            StudentName = "Test Child",
            SentAt = new DateTime(2026, 3, 9, 8, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow
        });

        _db.NewsItems.Add(new NewsItem
        {
            ParentId = _testParent.Id,
            SourceMessageId = message.ExternalMessageId,
            SourceType = SourceType.MessageText,
            NewsContent = "Book Fair is on October 14.",
            AiSummary = "Book Fair announcement",
            FromName = "Principal",
            StudentName = "Test Child",
            SentAt = message.SentAt,
            AnalyzedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([message]);

        _mockCategorizer.Setup(x => x.CategorizeAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategorizationResult
            {
                IsNewsItself = true,
                Summary = "Book Fair announcement"
            });

        StubEventExtractor("Book Fair", new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));

        await _orchestrator.RunAsync(_testParent);

        _db.TrackedEvents.Should().BeEmpty();
    }

    /// <summary>
    /// Extraction runs outside the transaction that stored the news item, so a failure there must
    /// cost the run neither the news item nor the digest.
    /// </summary>
    [Fact]
    public async Task RunAsync_EventExtractionThrows_KeepsTheNewsItemAndStillSendsTheDigest()
    {
        var message = await SeedUnprocessedMessageAsync(new Message
        {
            ExternalMessageId = "msg-extract-fails",
            FromName = "Principal",
            MessageText = "Book Fair is on October 14.",
            StudentName = "Test Child",
            SentAt = new DateTime(2026, 3, 9, 8, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow
        });

        _mockDeduplicator.Setup(x => x.GetUnprocessedAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([message]);

        _mockCategorizer.Setup(x => x.CategorizeAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategorizationResult { IsNewsItself = true, Summary = "Book Fair" });

        _mockEventExtractor.Setup(x => x.ExtractAsync(It.IsAny<NewsItem>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("extraction gateway down"));

        SetupDigest("the-prompt", "# Weekly Summary\nContent here");

        await _orchestrator.RunAsync(_testParent);

        _db.NewsItems.Should().ContainSingle();
        var summary = await _db.Summaries.SingleAsync();
        summary.Content.Should().Be("# Weekly Summary\nContent here");
        summary.EmailSentAt.Should().Be(FixedUtcNow.UtcDateTime);
    }

    /// <summary>
    /// The only write of IncludedInSummaryId in the system. Without it, every news item stays
    /// eligible forever and each week's digest re-reports every story ever fetched.
    /// </summary>
    [Fact]
    public async Task RunAsync_DigestDelivered_MarksItsNewsItemsReportedAndStampsEmailSentAt()
    {
        var reported = await SeedNewsItemsAsync("First story", "Second story");
        var untouched = await SeedNewsItemsAsync("Not in this digest");

        SetupDigest("the-prompt", "# Weekly Summary\nContent here", reported);

        await _orchestrator.RunAsync(_testParent);

        var summary = await _db.Summaries.SingleAsync();
        summary.EmailSentAt.Should().Be(FixedUtcNow.UtcDateTime);

        var stored = await _db.NewsItems.ToListAsync();
        stored.Where(item => reported.Any(fed => fed.Id == item.Id))
            .Should().OnlyContain(item => item.IncludedInSummaryId == summary.Id);
        stored.Single(item => item.Id == untouched[0].Id).IncludedInSummaryId.Should().BeNull();
    }

    /// <summary>
    /// A digest costs a full model call. A mail server that is down must not throw it away, and it
    /// must not bury the week's news behind a digest that never arrived either.
    /// </summary>
    [Fact]
    public async Task RunAsync_EmailSendFails_PersistsTheContentButLeavesTheNewsItemsUnreported()
    {
        var fed = await SeedNewsItemsAsync("First story");

        SetupDigest("the-prompt", "# Weekly Summary\nContent here", fed);

        _mockEmailSender.Setup(x => x.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP unreachable"));

        var act = async () => await _orchestrator.RunAsync(_testParent);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var summary = await _db.Summaries.SingleAsync();
        summary.Content.Should().Be("# Weekly Summary\nContent here");
        summary.EmailSentAt.Should().BeNull();

        var newsItem = await _db.NewsItems.SingleAsync();
        newsItem.IncludedInSummaryId.Should().BeNull(
            "an undelivered digest reported nothing, so its news items are still owed to the parent");
    }

    /// <summary>
    /// The validator exists to stop a wrong weekday or a past "upcoming" date reaching the parent.
    /// A validator that is registered but never invoked stops nothing.
    /// </summary>
    [Fact]
    public async Task RunAsync_ValidatorFindsAPastUpcomingDate_RevisesBeforeSending()
    {
        // 2026-03-01 is before the fixed current date of 2026-03-10, so it is not upcoming.
        const string Draft = "# Digest\n\n## Important Upcoming Dates\n- **Sunday, March 1, 2026** - Book Fair\n";
        const string Revised = "# Digest\n\n## Important Upcoming Dates\n- **Friday, March 20, 2026** - Book Fair\n";

        SetupDigest("the-prompt", Draft);
        _mockSummaryGenerator.Setup(x => x.ReviseAsync(It.IsAny<SummaryRevisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Revised);

        await _orchestrator.RunAsync(_testParent);

        var summary = await _db.Summaries.SingleAsync();
        summary.Content.Should().Be(Revised);
        summary.RevisionCount.Should().Be(1);
        summary.CritiqueLog.Should().NotBeNull();
        summary.CritiqueLog.Should().Contain("PastUpcomingDate");
        summary.CritiqueLog.Should().Contain("\"revised\":true");
    }

    /// <summary>
    /// A reviser that mangles the digest must not be able to make the emailed result worse than
    /// what generation produced.
    /// </summary>
    [Fact]
    public async Task RunAsync_RevisionIsWorseThanTheDraft_SendsTheOriginalDraft()
    {
        const string Draft = "# Digest\n\n## Important Upcoming Dates\n- **Sunday, March 1, 2026** - Book Fair\n";
        const string Worse = "# Digest\n\n## Important Upcoming Dates\n"
            + "- **Sunday, March 1, 2026** - Book Fair\n"
            + "- **Monday, March 2, 2026** - Picture Day\n";

        SetupDigest("the-prompt", Draft);
        _mockSummaryGenerator.Setup(x => x.ReviseAsync(It.IsAny<SummaryRevisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Worse);

        await _orchestrator.RunAsync(_testParent);

        var summary = await _db.Summaries.SingleAsync();
        summary.Content.Should().Be(Draft);
        summary.RevisionCount.Should().Be(0);
        summary.CritiqueLog.Should().Contain("\"revised\":false");
    }

    /// <summary>
    /// The critic catches what no code check can: a relative date in the source resolved to the
    /// wrong absolute date. Its findings have to reach the reviser.
    /// </summary>
    [Fact]
    public async Task RunAsync_CriticReportsAHighSeverityDefect_RevisesBeforeSending()
    {
        const string Draft = "# Digest\n\nThe assembly is on Friday, March 13, 2026.\n";
        const string Revised = "# Digest\n\nThe assembly is on Thursday, March 12, 2026.\n";

        SetupDigest("the-prompt", Draft);

        _mockCritic.Setup(x => x.CritiqueAsync(It.IsAny<SummaryCritiqueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CritiqueFinding(
                    CritiqueSeverity.High,
                    CritiqueFindingKinds.UnresolvedRelativeDate,
                    "Friday, March 13, 2026",
                    "The source said \"this Thursday\" and was sent on March 9, 2026.",
                    "Thursday, March 12, 2026")
            ]);

        SummaryRevisionRequest? revisionRequest = null;
        _mockSummaryGenerator.Setup(x => x.ReviseAsync(It.IsAny<SummaryRevisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SummaryRevisionRequest, CancellationToken>((request, _) => revisionRequest = request)
            .ReturnsAsync(Revised);

        await _orchestrator.RunAsync(_testParent);

        revisionRequest.Should().NotBeNull();
        revisionRequest!.DraftMarkdown.Should().Be(Draft);
        revisionRequest.Issues.Should().Contain(CritiqueFindingKinds.UnresolvedRelativeDate);
        revisionRequest.Issues.Should().Contain("Thursday, March 12, 2026");

        var summary = await _db.Summaries.SingleAsync();
        summary.Content.Should().Be(Revised);
        summary.RevisionCount.Should().Be(1);
        summary.CritiqueLog.Should().Contain(CritiqueFindingKinds.UnresolvedRelativeDate);
    }

    /// <summary>
    /// A low-severity finding is redundant or trivially wrong content. Spending a second full
    /// model call on it, and risking a reviser that rewrites more than it was asked to, costs more
    /// than the defect does.
    /// </summary>
    [Fact]
    public async Task RunAsync_OnlyLowSeverityCritiqueFindings_SendsTheDraftUnrevised()
    {
        const string Draft = "# Digest\n\nThe assembly is on Thursday, March 12, 2026.\n";

        SetupDigest("the-prompt", Draft);

        _mockCritic.Setup(x => x.CritiqueAsync(It.IsAny<SummaryCritiqueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CritiqueFinding(
                    CritiqueSeverity.Low,
                    CritiqueFindingKinds.Repeat,
                    "The assembly",
                    "Mentioned once already.",
                    string.Empty)
            ]);

        // Set up so that any revision at all would be visible in the stored content.
        _mockSummaryGenerator.Setup(x => x.ReviseAsync(It.IsAny<SummaryRevisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Rewritten digest\n");

        await _orchestrator.RunAsync(_testParent);

        var summary = await _db.Summaries.SingleAsync();
        summary.Content.Should().Be(Draft);
        summary.RevisionCount.Should().Be(0);
        summary.CritiqueLog.Should().Contain(CritiqueFindingKinds.Repeat);
    }

    /// <summary>
    /// A clean digest costs no revision call and leaves no critique log to read through.
    /// </summary>
    [Fact]
    public async Task RunAsync_NoFindings_LeavesTheCritiqueLogNull()
    {
        SetupDigest("the-prompt", "# Digest\n\nThe assembly is on Thursday, March 12, 2026.\n");

        await _orchestrator.RunAsync(_testParent);

        var summary = await _db.Summaries.SingleAsync();
        summary.CritiqueLog.Should().BeNull();
        summary.RevisionCount.Should().Be(0);
    }

    /// <summary>
    /// The critic reviews the digest against the same sources and rendered blocks the generator
    /// built the prompt from, not a re-derivation of them.
    /// </summary>
    [Fact]
    public async Task RunAsync_CriticIsGivenTheSourceItemsAndRenderedContextFromThePrompt()
    {
        var fed = await SeedNewsItemsAsync("First story");

        SummaryCritiqueRequest? critiqueRequest = null;
        _mockCritic.Setup(x => x.CritiqueAsync(It.IsAny<SummaryCritiqueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SummaryCritiqueRequest, CancellationToken>((request, _) => critiqueRequest = request)
            .ReturnsAsync([]);

        _mockSummaryGenerator.Setup(x => x.BuildPromptAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SummaryPromptResult(
                "the-prompt",
                fed,
                "### Test School\n- **Friday, March 20, 2026** - Book Fair",
                "2026-03-02 digest: Spring Concert"));
        _mockSummaryGenerator.Setup(x => x.ExecutePromptAsync("the-prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Digest\n\nBody.");

        await _orchestrator.RunAsync(_testParent);

        critiqueRequest.Should().NotBeNull();
        critiqueRequest!.DraftMarkdown.Should().Be("# Digest\n\nBody.");
        critiqueRequest.SourceItems.Select(item => item.Id).Should().Equal(fed[0].Id);
        critiqueRequest.ActiveEvents.Should().Contain("Book Fair");
        critiqueRequest.CoverageLedger.Should().Contain("Spring Concert");
    }

    [Fact]
    public void FormatIssues_RendersBothFindingSetsAsSeparatelyLabelledBlocks()
    {
        var issues = PipelineOrchestrator.FormatIssues(
            [
                new SummaryValidationFinding(
                    SummaryValidationFindingKind.WeekdayDateMismatch,
                    4,
                    "- **Friday, May 15, 2026** - Field Day",
                    "Output says \"Friday, May 15, 2026\", but May 15, 2026 falls on a Friday.")
            ],
            [
                new CritiqueFinding(
                    CritiqueSeverity.High,
                    CritiqueFindingKinds.UnsupportedClaim,
                    "Tickets cost $12",
                    "No source item mentions a price.",
                    "Delete the price.")
            ]);

        issues.Should().Contain("Date and formatting defects found by the validator:");
        issues.Should().Contain("1. Line 4 (WeekdayDateMismatch):");
        issues.Should().Contain("Factual defects found by the reviewer:");
        issues.Should().Contain("1. [High] unsupported-claim: No source item mentions a price.");
        issues.Should().Contain("Text: \"Tickets cost $12\"");
        issues.Should().Contain("Fix: Delete the price.");
    }

    [Fact]
    public void FormatIssues_NoFindings_RendersNothing()
    {
        PipelineOrchestrator.FormatIssues([], []).Should().BeEmpty();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    /// <summary>
    /// Makes the summary generator produce one digest, optionally built from the supplied items.
    /// </summary>
    private void SetupDigest(string prompt, string markdown, IReadOnlyList<NewsItem>? newsItems = null)
    {
        _mockSummaryGenerator.Setup(x => x.BuildPromptAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SummaryPromptResult(prompt, newsItems ?? []));
        _mockSummaryGenerator.Setup(x => x.ExecutePromptAsync(prompt, It.IsAny<CancellationToken>()))
            .ReturnsAsync(markdown);
        _mockMarkdownConverter.Setup(x => x.ToHtml(It.IsAny<string>()))
            .Returns<string>(value => "<html>" + value + "</html>");
    }

    /// <summary>
    /// Stands in for the real extractor by writing the tracked event a real one would, so tests
    /// can assert on persisted state instead of on the mock having been called.
    /// </summary>
    private void StubEventExtractor(string title, DateTime eventDate)
    {
        _mockEventExtractor
            .Setup(x => x.ExtractAsync(It.IsAny<NewsItem>(), It.IsAny<CancellationToken>()))
            .Returns(async (NewsItem newsItem, CancellationToken ct) =>
            {
                var trackedEvent = new TrackedEvent
                {
                    ParentId = newsItem.ParentId,
                    SourceNewsItemId = newsItem.Id,
                    School = "Test School",
                    EventDate = eventDate,
                    Title = title,
                    Status = TrackedEventStatus.Active,
                    CreatedAt = FixedUtcNow.UtcDateTime
                };
                _db.TrackedEvents.Add(trackedEvent);
                await _db.SaveChangesAsync(ct);
                return (IReadOnlyList<TrackedEvent>)[trackedEvent];
            });
    }

    private async Task<List<NewsItem>> SeedNewsItemsAsync(params string[] contents)
    {
        var items = contents.Select(content => new NewsItem
        {
            ParentId = _testParent.Id,
            SourceMessageId = Guid.NewGuid().ToString(),
            SourceType = SourceType.MessageText,
            NewsContent = content,
            AiSummary = content,
            FromName = "Teacher",
            StudentName = "Test Child",
            SentAt = FixedUtcNow.UtcDateTime.AddDays(-1),
            AnalyzedAt = FixedUtcNow.UtcDateTime,
            CreatedAt = FixedUtcNow.UtcDateTime
        }).ToList();

        _db.NewsItems.AddRange(items);
        await _db.SaveChangesAsync();
        return items;
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
