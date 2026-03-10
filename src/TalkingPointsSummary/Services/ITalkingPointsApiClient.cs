using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

public interface ITalkingPointsApiClient
{
    Task<List<TalkingPointsMessage>> FetchMessagesAsync(Parent parent, CancellationToken ct = default);
}
