using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Filters out messages that already exist in the database and saves new ones.
/// </summary>
public class MessageDeduplicator : IMessageDeduplicator
{
    private readonly AppDbContext _db;
    private readonly ILogger<MessageDeduplicator> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a deduplicator for storing new TalkingPoints messages.
    /// </summary>
    /// <param name="db">Database context used for persistence.</param>
    /// <param name="logger">Logger used for deduplication diagnostics.</param>
    /// <param name="timeProvider">Optional time provider used for local timestamps.</param>
    public MessageDeduplicator(AppDbContext db, ILogger<MessageDeduplicator> logger, TimeProvider? timeProvider = null)
    {
        _db = db;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Takes raw API messages, filters out duplicates, saves new ones to DB, and returns the saved messages.
    /// </summary>
    public async Task<List<Message>> DeduplicateAndSaveAsync(
        Parent parent,
        List<TalkingPointsMessage> apiMessages,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcDateTime();

        var existingIds = (await _db.Messages
            .Where(m => m.ParentId == parent.Id)
            .Select(m => m.ExternalMessageId)
            .ToListAsync(ct))
            .ToHashSet();

        var newMessages = new List<Message>();

        foreach (var apiMsg in apiMessages)
        {
            if (existingIds.Contains(apiMsg.Id))
                continue;

            var apiSentAt = apiMsg.DisplayDate ?? apiMsg.CreatedAt;
            if (apiSentAt is null)
            {
                _logger.LogWarning(
                    "Message {ExternalMessageId} for parent {ParentName} has no DisplayDate or CreatedAt from the API; " +
                    "using the current fetch time as the sent timestamp. Relative date references in this message may be misdated in the digest.",
                    apiMsg.Id, parent.Name);
            }

            var message = new Message
            {
                ParentId = parent.Id,
                ExternalMessageId = apiMsg.Id,
                ContactMessageId = apiMsg.ContactMessageId ?? string.Empty,
                StudentName = apiMsg.ContactInfo?.StudentName ?? string.Empty,
                FromName = apiMsg.From?.User?.Signature ?? apiMsg.FromName ?? string.Empty,
                MessageText = apiMsg.Text ?? string.Empty,
                SentAt = apiSentAt ?? now,
                CreatedAt = now
            };

            newMessages.Add(message);
        }

        if (newMessages.Count > 0)
        {
            _db.Messages.AddRange(newMessages);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Saved {Count} new messages for parent {ParentName}", newMessages.Count, parent.Name);
        }
        else
        {
            _logger.LogInformation("No new messages for parent {ParentName}", parent.Name);
        }

        return newMessages;
    }

    /// <summary>
    /// Returns all unprocessed messages for a parent.
    /// </summary>
    public async Task<List<Message>> GetUnprocessedAsync(Parent parent, CancellationToken ct = default)
    {
        return await _db.Messages
            .Where(m => m.ParentId == parent.Id && m.ProcessedAt == null)
            .OrderBy(m => m.SentAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Marks a message as processed.
    /// </summary>
    public async Task MarkProcessedAsync(Message message, CancellationToken ct = default)
    {
        message.ProcessedAt = _timeProvider.GetUtcDateTime();
        await _db.SaveChangesAsync(ct);
    }
}
