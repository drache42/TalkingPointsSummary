namespace TalkingPointsSummary.Models;

public class Parent
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TalkingPointsToken { get; set; } = string.Empty;
    public string TalkingPointsContactId { get; set; } = string.Empty;

    /// <summary>
    /// Semicolon-delimited list of email addresses to send summaries to.
    /// </summary>
    public string EmailRecipients { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public List<Child> Children { get; set; } = new();
    public List<Message> Messages { get; set; } = new();
    public List<NewsItem> NewsItems { get; set; } = new();
    public List<Summary> Summaries { get; set; } = new();
}
