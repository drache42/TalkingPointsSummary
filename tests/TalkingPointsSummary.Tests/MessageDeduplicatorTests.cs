using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class MessageDeduplicatorTests : IDisposable
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 3, 10, 14, 30, 0, TimeSpan.Zero);

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

        _deduplicator = new MessageDeduplicator(_db, NullLogger<MessageDeduplicator>.Instance, new FixedTimeProvider(FixedUtcNow));
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
                ContactInfo = new TalkingPointsContactInfo { StudentName = "StudentOne" },
                DisplayDate = DateTime.UtcNow
            }
        };

        var result = await _deduplicator.DeduplicateAndSaveAsync(_testParent, apiMessages);

        result.Should().HaveCount(1);
        result[0].ExternalMessageId.Should().Be("msg-001");
        result[0].MessageText.Should().Be("Hello parents!");
        result[0].CreatedAt.Should().Be(FixedUtcNow.UtcDateTime);

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

        message.ProcessedAt.Should().Be(FixedUtcNow.UtcDateTime);
    }

    [Fact]
    public async Task GetUnprocessedAsync_CrossParentIsolation_DoesNotReturnOtherParentsMessages()
    {
        var otherParent = new Parent
        {
            Name = "Other Parent",
            TalkingPointsToken = "other-token",
            TalkingPointsContactId = "other-contact",
            EmailRecipients = "other@example.com"
        };
        _db.Parents.Add(otherParent);
        await _db.SaveChangesAsync();

        _db.Messages.AddRange(
            new Message { ParentId = _testParent.Id, ExternalMessageId = "msg-mine", MessageText = "Mine", SentAt = DateTime.UtcNow },
            new Message { ParentId = otherParent.Id, ExternalMessageId = "msg-other", MessageText = "Other", SentAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync();

        var result = await _deduplicator.GetUnprocessedAsync(_testParent);

        result.Should().HaveCount(1);
        result[0].ExternalMessageId.Should().Be("msg-mine");
    }

    [Fact]
    public async Task GetUnprocessedAsync_OrdersBySentAt()
    {
        _db.Messages.AddRange(
            new Message { ParentId = _testParent.Id, ExternalMessageId = "msg-later", MessageText = "Later", SentAt = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc) },
            new Message { ParentId = _testParent.Id, ExternalMessageId = "msg-earlier", MessageText = "Earlier", SentAt = new DateTime(2026, 3, 7, 8, 0, 0, DateTimeKind.Utc) },
            new Message { ParentId = _testParent.Id, ExternalMessageId = "msg-middle", MessageText = "Middle", SentAt = new DateTime(2026, 3, 8, 10, 0, 0, DateTimeKind.Utc) }
        );
        await _db.SaveChangesAsync();

        var result = await _deduplicator.GetUnprocessedAsync(_testParent);

        result.Should().HaveCount(3);
        result[0].ExternalMessageId.Should().Be("msg-earlier");
        result[1].ExternalMessageId.Should().Be("msg-middle");
        result[2].ExternalMessageId.Should().Be("msg-later");
    }

    [Fact]
    public async Task DeduplicateAndSaveAsync_MapsFieldsCorrectly()
    {
        var apiMessages = new List<TalkingPointsMessage>
        {
            new()
            {
                Id = "msg-fields",
                Text = "Hello parents!",
                FromName = "Direct FromName",
                From = new TalkingPointsFrom { User = new TalkingPointsUser { Signature = "Ms. Jane Smith" } },
                ContactInfo = new TalkingPointsContactInfo { StudentName = "StudentOne" },
                ContactMessageId = "contact-123",
                DisplayDate = new DateTime(2026, 3, 7, 10, 0, 0, DateTimeKind.Utc)
            }
        };

        var result = await _deduplicator.DeduplicateAndSaveAsync(_testParent, apiMessages);

        result.Should().HaveCount(1);
        var saved = result[0];
        saved.StudentName.Should().Be("StudentOne");
        saved.FromName.Should().Be("Ms. Jane Smith"); // Signature takes precedence over FromName
        saved.MessageText.Should().Be("Hello parents!");
        saved.ExternalMessageId.Should().Be("msg-fields");
        saved.ContactMessageId.Should().Be("contact-123");
        saved.SentAt.Should().Be(new DateTime(2026, 3, 7, 10, 0, 0, DateTimeKind.Utc));
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}
