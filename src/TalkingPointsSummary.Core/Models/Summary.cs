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
    /// The full Markdown summary content.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// UTC time when the summary was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Parent that owns the summary.
    /// </summary>
    public Parent Parent { get; set; } = null!;
}
