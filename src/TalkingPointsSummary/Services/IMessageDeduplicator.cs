using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Stores new messages while filtering out duplicates and tracking processing state.
/// </summary>
public interface IMessageDeduplicator
{
    /// <summary>
    /// Saves new API messages that are not already stored for the parent.
    /// </summary>
    /// <param name="parent">Parent that owns the messages.</param>
    /// <param name="apiMessages">Messages returned by the TalkingPoints API.</param>
    /// <param name="ct">Token used to cancel the operation.</param>
    Task<List<Message>> DeduplicateAndSaveAsync(Parent parent, List<TalkingPointsMessage> apiMessages, CancellationToken ct = default);

    /// <summary>
    /// Returns stored messages for the parent that have not yet been processed.
    /// </summary>
    /// <param name="parent">Parent whose messages should be returned.</param>
    /// <param name="ct">Token used to cancel the operation.</param>
    Task<List<Message>> GetUnprocessedAsync(Parent parent, CancellationToken ct = default);

    /// <summary>
    /// Marks a message as processed.
    /// </summary>
    /// <param name="message">Message to mark as processed.</param>
    /// <param name="ct">Token used to cancel the operation.</param>
    Task MarkProcessedAsync(Message message, CancellationToken ct = default);
}
