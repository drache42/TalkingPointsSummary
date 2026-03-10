namespace TalkingPointsSummary.Models;

/// <summary>
/// Child profile used to group school news in summaries.
/// </summary>
public class Child
{
    /// <summary>
    /// Database identifier for the child.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Identifier of the parent that owns the child.
    /// </summary>
    public int ParentId { get; set; }

    /// <summary>
    /// Display name for the child.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// School attended by the child.
    /// </summary>
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

    /// <summary>
    /// Parent that owns the child.
    /// </summary>
    public Parent Parent { get; set; } = null!;
}
