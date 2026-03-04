namespace TalkingPointsSummary.Models;

public class Child
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string School { get; set; } = string.Empty;

    /// <summary>
    /// Grade level as of StartingYear. 0 = Kindergarten, 1 = 1st Grade, etc.
    /// </summary>
    public int StartingGrade { get; set; }

    /// <summary>
    /// The school year when StartingGrade applies (e.g. 2025 means the 2025-2026 school year).
    /// </summary>
    public int StartingYear { get; set; }

    /// <summary>
    /// Emoji used in summary headings for this child (e.g. "📚", "🎓").
    /// </summary>
    public string Emoji { get; set; } = "📚";

    // Navigation
    public Parent Parent { get; set; } = null!;
}
