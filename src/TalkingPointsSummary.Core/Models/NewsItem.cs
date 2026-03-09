namespace TalkingPointsSummary.Models;

public enum SourceType
{
    MessageText,
    NewsletterUrl
}

public class NewsItem
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string SourceMessageId { get; set; } = string.Empty;
    public SourceType SourceType { get; set; }
    public string? NewsletterUrl { get; set; }

    /// <summary>
    /// Full news content — either the message text or scraped newsletter text.
    /// </summary>
    public string NewsContent { get; set; } = string.Empty;

    /// <summary>
    /// Brief AI-generated summary of this news item.
    /// </summary>
    public string AiSummary { get; set; } = string.Empty;

    public string FromName { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Parent Parent { get; set; } = null!;
}
