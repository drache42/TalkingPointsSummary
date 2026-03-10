using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

public interface IMessageDeduplicator
{
    Task<List<Message>> DeduplicateAndSaveAsync(Parent parent, List<TalkingPointsMessage> apiMessages, CancellationToken ct = default);
    Task<List<Message>> GetUnprocessedAsync(Parent parent, CancellationToken ct = default);
    Task MarkProcessedAsync(Message message, CancellationToken ct = default);
}
