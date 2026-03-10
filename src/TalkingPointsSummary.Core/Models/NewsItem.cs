namespace TalkingPointsSummary.Models;

/// <summary>
/// Indicates which source supplied a stored news item.
/// </summary>
public enum SourceType
{
    /// <summary>
    /// The news item content came directly from the message body.
    /// </summary>
    MessageText,

    /// <summary>
    /// The news item content came from a scraped newsletter URL.
    /// </summary>
    NewsletterUrl
}

/// <summary>
/// News extracted from a TalkingPoints message or linked newsletter.
/// </summary>
public class NewsItem
{
    /// <summary>
    /// Database identifier for the news item.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Identifier of the parent that owns the news item.
    /// </summary>
    public int ParentId { get; set; }

    /// <summary>
    /// External message identifier that produced the news item.
    /// </summary>
    public string SourceMessageId { get; set; } = string.Empty;

    /// <summary>
    /// Source used to populate the news content.
    /// </summary>
    public SourceType SourceType { get; set; }

    /// <summary>
    /// Newsletter URL when the news item came from a scraped newsletter.
    /// </summary>
    public string? NewsletterUrl { get; set; }

    /// <summary>
    /// Full news content — either the message text or scraped newsletter text.
    /// </summary>
    public string NewsContent { get; set; } = string.Empty;

    /// <summary>
    /// Brief AI-generated summary of this news item.
    /// </summary>
    public string AiSummary { get; set; } = string.Empty;

    /// <summary>
    /// Sender display name associated with the item.
    /// </summary>
    public string FromName { get; set; } = string.Empty;

    /// <summary>
    /// Student name associated with the item.
    /// </summary>
    public string StudentName { get; set; } = string.Empty;

    /// <summary>
    /// UTC time when the source message was sent.
    /// </summary>
    public DateTime SentAt { get; set; }

    /// <summary>
    /// UTC time when the item was analyzed and summarized.
    /// </summary>
    public DateTime AnalyzedAt { get; set; }

    /// <summary>
    /// UTC time when the item was created locally.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Parent that owns the news item.
    /// </summary>
    public Parent Parent { get; set; } = null!;
}
