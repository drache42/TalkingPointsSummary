using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

public interface ITalkingPointsApiClient
{
    Task<List<TalkingPointsMessage>> FetchMessagesAsync(
        Parent parent,
        string? stopAtMessageId = null,
        DateTime? stopBeforeSentAtUtc = null,
        int? maxPagesOverride = null,
        CancellationToken ct = default);
}
