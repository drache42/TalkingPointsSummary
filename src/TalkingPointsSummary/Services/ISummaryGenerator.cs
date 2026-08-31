using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Result of building a summary prompt, returned before the AI call is made.
/// </summary>
/// <param name="Prompt">The constructed prompt text ready to send to the AI.</param>
/// <param name="NewsItems">
/// The news items fed into the prompt, in the order they were rendered. The caller needs the
/// rows themselves, not just a count: they are what gets stamped with
/// <see cref="NewsItem.IncludedInSummaryId"/> once the digest built from them is delivered, and
/// they are the source set the critic checks the draft against.
/// </param>
/// <param name="UpcomingDates">
/// The pre-rendered "Important Upcoming Dates" section handed to the model, or
/// <see langword="null"/> when there was nothing to render. Carried out of the builder so the
/// critic and the reviser see the exact list the draft was written from.
/// </param>
/// <param name="CoverageLedger">
/// The rendered index of what earlier digests already delivered, or <see langword="null"/> when
/// there were none.
/// </param>
public sealed record SummaryPromptResult(
    string Prompt,
    IReadOnlyList<NewsItem> NewsItems,
    string? UpcomingDates = null,
    string? CoverageLedger = null)
{
    /// <summary>
    /// Number of news items included in the prompt.
    /// </summary>
    public int NewsItemCount => NewsItems.Count;
}

/// <summary>
/// A draft digest and the defects reported against it, sent back to the model for correction.
/// </summary>
/// <param name="DraftMarkdown">The digest markdown to revise.</param>
/// <param name="Issues">
/// The reported defects, already rendered as prompt-ready text. Each one names where the problem
/// is and what the correction should be.
/// </param>
/// <param name="UpcomingDates">
/// The authoritative pre-rendered upcoming dates section, so the reviser reproduces it rather
/// than rebuilding it from the draft. Null renders as "None".
/// </param>
public sealed record SummaryRevisionRequest(
    string DraftMarkdown,
    string Issues,
    string? UpcomingDates = null);

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

    /// <summary>
    /// Sends a draft digest back to the AI together with the defects reported against it and
    /// returns the corrected Markdown.
    /// </summary>
    /// <remarks>
    /// A revision is an improvement pass, not a gate. Returning <see langword="null"/> means the
    /// caller should keep and send the draft it already has, so every failure short of
    /// caller-requested cancellation, including a truncated or empty completion, is reported that
    /// way rather than thrown.
    /// </remarks>
    /// <param name="request">Draft digest and the defects to correct.</param>
    /// <param name="ct">Token used to cancel the AI call.</param>
    /// <returns>The revised Markdown, or <see langword="null"/> when no usable revision came back.</returns>
    Task<string?> ReviseAsync(SummaryRevisionRequest request, CancellationToken ct = default);
}
