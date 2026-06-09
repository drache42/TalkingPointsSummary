using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Result of building a summary prompt, returned before the AI call is made.
/// </summary>
/// <param name="Prompt">The constructed prompt text ready to send to the AI.</param>
/// <param name="NewsItemCount">Number of news items included in the prompt.</param>
public record SummaryPromptResult(string Prompt, int NewsItemCount);

/// <summary>
/// Generates weekly Markdown summaries for a parent.
/// </summary>
public interface ISummaryGenerator
{
    /// <summary>
    /// Loads news, children, and prior summaries for the parent and builds the AI prompt.
    /// Returns <see langword="null"/> when there are no news items to summarize.
    /// </summary>
    /// <param name="parent">Parent to build the prompt for.</param>
    /// <param name="ct">Token used to cancel the operation.</param>
    Task<SummaryPromptResult?> BuildPromptAsync(Parent parent, CancellationToken ct = default);

    /// <summary>
    /// Sends the prompt to the AI and returns the generated Markdown, or
    /// <see langword="null"/> when the model returns an empty response.
    /// </summary>
    /// <param name="prompt">Prompt text previously built by <see cref="BuildPromptAsync"/>.</param>
    /// <param name="ct">Token used to cancel the AI call.</param>
    Task<string?> ExecutePromptAsync(string prompt, CancellationToken ct = default);
}
