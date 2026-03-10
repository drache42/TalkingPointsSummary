namespace TalkingPointsSummary.Models;

/// <summary>
/// Parent account configuration used to fetch messages and send summaries.
/// </summary>
public class Parent
{
    /// <summary>
    /// Database identifier for the parent.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Display name for the parent.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// TalkingPoints API token associated with the parent.
    /// </summary>
    public string TalkingPointsToken { get; set; } = string.Empty;

    /// <summary>
    /// TalkingPoints contact identifier associated with the parent.
    /// </summary>
    public string TalkingPointsContactId { get; set; } = string.Empty;

    /// <summary>
    /// Semicolon-delimited list of email addresses to send summaries to.
    /// </summary>
    public string EmailRecipients { get; set; } = string.Empty;

    /// <summary>
    /// Whether the parent is eligible for pipeline processing.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// UTC timestamp when the parent record was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Children associated with the parent.
    /// </summary>
    public List<Child> Children { get; set; } = new();

    /// <summary>
    /// Messages fetched for the parent.
    /// </summary>
    public List<Message> Messages { get; set; } = new();

    /// <summary>
    /// News items extracted for the parent.
    /// </summary>
    public List<NewsItem> NewsItems { get; set; } = new();

    /// <summary>
    /// Generated summaries associated with the parent.
    /// </summary>
    public List<Summary> Summaries { get; set; } = new();
}
