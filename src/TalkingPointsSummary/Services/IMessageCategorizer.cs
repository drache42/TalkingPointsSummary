using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

public interface IMessageCategorizer
{
    Task<CategorizationResult> CategorizeAsync(Message message, CancellationToken ct = default);
}
