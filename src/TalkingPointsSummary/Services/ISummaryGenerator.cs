using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Generates weekly Markdown summaries for a parent.
/// </summary>
public interface ISummaryGenerator
{
    /// <summary>
    /// Generates a summary for the supplied parent, or <see langword="null"/> when no summary should be produced.
    /// </summary>
    /// <param name="parent">Parent to summarize.</param>
    /// <param name="ct">Token used to cancel generation.</param>
    Task<string?> GenerateAsync(Parent parent, CancellationToken ct = default);
}
