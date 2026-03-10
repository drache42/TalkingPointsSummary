using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Categorizes messages as direct news, newsletter links, or non-news content.
/// </summary>
public interface IMessageCategorizer
{
    /// <summary>
    /// Categorizes a stored message.
    /// </summary>
    /// <param name="message">Message to categorize.</param>
    /// <param name="ct">Token used to cancel the categorization request.</param>
    Task<CategorizationResult> CategorizeAsync(Message message, CancellationToken ct = default);
}
