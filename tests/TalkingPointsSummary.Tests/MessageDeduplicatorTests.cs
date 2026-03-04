using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class MessageDeduplicatorTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly MessageDeduplicator _deduplicator;
    private readonly Parent _testParent;

    public MessageDeduplicatorTests()
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
        _db.SaveChanges();

        _deduplicator = new MessageDeduplicator(_db, NullLogger<MessageDeduplicator>.Instance);
    }

    [Fact]
    public async Task DeduplicateAndSaveAsync_SavesNewMessages()
    {
        var apiMessages = new List<TalkingPointsMessage>
        {
            new()
            {
                Id = "msg-001",
                Text = "Hello parents!",
                FromName = "Teacher",
                ContactInfo = new TalkingPointsContactInfo { StudentName = "Clara" },
                DisplayDate = DateTime.UtcNow
            }
        };

        var result = await _deduplicator.DeduplicateAndSaveAsync(_testParent, apiMessages);

        result.Should().HaveCount(1);
        result[0].ExternalMessageId.Should().Be("msg-001");
        result[0].MessageText.Should().Be("Hello parents!");

        _db.Messages.Should().HaveCount(1);
    }

    [Fact]
    public async Task DeduplicateAndSaveAsync_SkipsExistingMessages()
    {
        // Pre-populate an existing message
        _db.Messages.Add(new Message
        {
            ParentId = _testParent.Id,
            ExternalMessageId = "msg-001",
            MessageText = "Already stored",
            SentAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var apiMessages = new List<TalkingPointsMessage>
        {
            new() { Id = "msg-001", Text = "Hello parents!", DisplayDate = DateTime.UtcNow },
            new() { Id = "msg-002", Text = "New message!", DisplayDate = DateTime.UtcNow }
        };

        var result = await _deduplicator.DeduplicateAndSaveAsync(_testParent, apiMessages);

        result.Should().HaveCount(1);
        result[0].ExternalMessageId.Should().Be("msg-002");

        _db.Messages.Should().HaveCount(2); // 1 existing + 1 new
    }

    [Fact]
    public async Task DeduplicateAndSaveAsync_ReturnsEmptyWhenAllDuplicates()
    {
        _db.Messages.Add(new Message
        {
            ParentId = _testParent.Id,
            ExternalMessageId = "msg-001",
            MessageText = "Already stored",
            SentAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var apiMessages = new List<TalkingPointsMessage>
        {
            new() { Id = "msg-001", Text = "Hello parents!", DisplayDate = DateTime.UtcNow }
        };

        var result = await _deduplicator.DeduplicateAndSaveAsync(_testParent, apiMessages);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUnprocessedAsync_ReturnsOnlyUnprocessedMessages()
    {
        _db.Messages.AddRange(
            new Message
            {
                ParentId = _testParent.Id,
                ExternalMessageId = "msg-001",
                MessageText = "Processed",
                SentAt = DateTime.UtcNow,
                ProcessedAt = DateTime.UtcNow
            },
            new Message
            {
                ParentId = _testParent.Id,
                ExternalMessageId = "msg-002",
                MessageText = "Unprocessed",
                SentAt = DateTime.UtcNow,
                ProcessedAt = null
            }
        );
        await _db.SaveChangesAsync();

        var result = await _deduplicator.GetUnprocessedAsync(_testParent);

        result.Should().HaveCount(1);
        result[0].ExternalMessageId.Should().Be("msg-002");
    }

    [Fact]
    public async Task MarkProcessedAsync_SetsProcessedAt()
    {
        var message = new Message
        {
            ParentId = _testParent.Id,
            ExternalMessageId = "msg-001",
            MessageText = "Test",
            SentAt = DateTime.UtcNow
        };
        _db.Messages.Add(message);
        await _db.SaveChangesAsync();

        message.ProcessedAt.Should().BeNull();

        await _deduplicator.MarkProcessedAsync(message);

        message.ProcessedAt.Should().NotBeNull();
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}
