using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class PipelineOrchestratorTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly MessageDeduplicator _deduplicator;
    private readonly MarkdownConverter _markdownConverter;
    private readonly Parent _testParent;

    public PipelineOrchestratorTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
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

        _deduplicator = new MessageDeduplicator(_db, NullLogger<MessageDeduplicator>.Instance);
        _markdownConverter = new MarkdownConverter();
    }

    [Fact]
    public async Task Deduplicator_CorrectlyFiltersAndSavesMessages()
    {
        // Pre-populate existing message
        _db.Messages.Add(new Message
        {
            ParentId = _testParent.Id,
            ExternalMessageId = "existing-001",
            MessageText = "Old message",
            SentAt = DateTime.UtcNow,
            ProcessedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var apiMessages = new List<TalkingPointsMessage>
        {
            new() { Id = "existing-001", Text = "Old message", DisplayDate = DateTime.UtcNow },
            new() { Id = "new-001", Text = "New message", DisplayDate = DateTime.UtcNow,
                    ContactInfo = new TalkingPointsContactInfo { StudentName = "Clara" } }
        };

        var saved = await _deduplicator.DeduplicateAndSaveAsync(_testParent, apiMessages);

        saved.Should().HaveCount(1);
        saved[0].ExternalMessageId.Should().Be("new-001");
    }

    [Fact]
    public async Task Deduplicator_ProcessedFlow_WorksEndToEnd()
    {
        // Save a new message
        var apiMessages = new List<TalkingPointsMessage>
        {
            new() { Id = "flow-001", Text = "Test message", DisplayDate = DateTime.UtcNow }
        };

        var saved = await _deduplicator.DeduplicateAndSaveAsync(_testParent, apiMessages);
        saved.Should().HaveCount(1);

        // Get unprocessed
        var unprocessed = await _deduplicator.GetUnprocessedAsync(_testParent);
        unprocessed.Should().HaveCount(1);

        // Mark processed
        await _deduplicator.MarkProcessedAsync(unprocessed[0]);

        // Should be empty now
        var remaining = await _deduplicator.GetUnprocessedAsync(_testParent);
        remaining.Should().BeEmpty();
    }

    [Fact]
    public async Task NewsItems_CanBeSavedAndQueried()
    {
        var newsItem = new NewsItem
        {
            ParentId = _testParent.Id,
            SourceMessageId = "msg-001",
            SourceType = SourceType.MessageText,
            NewsContent = "School picture day is Friday",
            AiSummary = "Picture day announcement",
            FromName = "Teacher",
            StudentName = "Clara",
            SentAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            AnalyzedAt = DateTime.UtcNow
        };

        _db.NewsItems.Add(newsItem);
        await _db.SaveChangesAsync();

        var items = await _db.NewsItems
            .Where(n => n.ParentId == _testParent.Id)
            .ToListAsync();

        items.Should().HaveCount(1);
        items[0].SourceType.Should().Be(SourceType.MessageText);
        items[0].NewsContent.Should().Be("School picture day is Friday");
    }

    [Fact]
    public async Task Summaries_CanBeSavedAndQueried()
    {
        var summary = new Summary
        {
            ParentId = _testParent.Id,
            Content = "# Weekly Summary\nTest content",
            CreatedAt = DateTime.UtcNow
        };

        _db.Summaries.Add(summary);
        await _db.SaveChangesAsync();

        var summaries = await _db.Summaries
            .Where(s => s.ParentId == _testParent.Id)
            .ToListAsync();

        summaries.Should().HaveCount(1);
        summaries[0].Content.Should().Contain("Weekly Summary");
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}
