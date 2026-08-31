using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Extracts dated school events from a news item and persists them as tracked events.
/// </summary>
public interface IEventExtractor
{
    /// <summary>
    /// Extracts calendar events from a single news item, persists the new ones, and applies
    /// any replacement or cancellation the news item announced for already-tracked events.
    /// </summary>
    /// <param name="newsItem">Persisted news item to scan for dated events.</param>
    /// <param name="ct">Token used to cancel the extraction request.</param>
    /// <returns>The tracked events newly created by this call, empty when nothing was extracted.</returns>
    Task<IReadOnlyList<TrackedEvent>> ExtractAsync(NewsItem newsItem, CancellationToken ct = default);
}
