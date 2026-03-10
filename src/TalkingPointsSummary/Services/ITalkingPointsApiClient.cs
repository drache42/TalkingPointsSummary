using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Fetches paged TalkingPoints messages for a parent account.
/// </summary>
public interface ITalkingPointsApiClient
{
    /// <summary>
    /// Fetches messages for a parent until pagination stops or a stopping condition is met.
    /// </summary>
    /// <param name="parent">Parent whose TalkingPoints credentials should be used.</param>
    /// <param name="stopAtMessageId">Optional external message identifier that ends pagination when encountered.</param>
    /// <param name="stopBeforeSentAtUtc">Optional sent-at threshold that ends pagination when older messages are reached.</param>
    /// <param name="maxPagesOverride">Optional override for the maximum number of pages to request.</param>
    /// <param name="ct">Token used to cancel the fetch.</param>
    Task<List<TalkingPointsMessage>> FetchMessagesAsync(
        Parent parent,
        string? stopAtMessageId = null,
        DateTime? stopBeforeSentAtUtc = null,
        int? maxPagesOverride = null,
        CancellationToken ct = default);
}
