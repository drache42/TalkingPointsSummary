namespace TalkingPointsSummary.Models;

/// <summary>
/// Generated weekly summary content for a parent.
/// </summary>
public class Summary
{
    /// <summary>
    /// Database identifier for the summary.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Identifier of the parent that owns the summary.
    /// </summary>
    public int ParentId { get; set; }

    /// <summary>
    /// The AI prompt used to generate this summary. Persisted before the AI call
    /// so it is available for debugging even when generation fails.
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>
    /// The full Markdown summary content. Null while generation is in progress
    /// or when the AI call failed after the prompt was saved.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// UTC time when the summary was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Parent that owns the summary.
    /// </summary>
    public Parent Parent { get; set; } = null!;
}
