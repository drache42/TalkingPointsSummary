namespace TalkingPointsSummary.Models;

public class Summary
{
    public int Id { get; set; }
    public int ParentId { get; set; }

    /// <summary>
    /// The full Markdown summary content.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Parent Parent { get; set; } = null!;
}
