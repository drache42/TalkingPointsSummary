namespace TalkingPointsSummary.Models;

public class Message
{
    public int Id { get; set; }
    public int ParentId { get; set; }

    /// <summary>
    /// The _id from the TalkingPoints API response.
    /// </summary>
    public string ExternalMessageId { get; set; } = string.Empty;

    public string ContactMessageId { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string MessageText { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Null until the message has been categorized and routed to news/newsletter.
    /// </summary>
    public DateTime? ProcessedAt { get; set; }

    // Navigation
    public Parent Parent { get; set; } = null!;
}
