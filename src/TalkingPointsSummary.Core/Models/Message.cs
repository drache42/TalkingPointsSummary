namespace TalkingPointsSummary.Models;

/// <summary>
/// Raw TalkingPoints message stored for processing and deduplication.
/// </summary>
public class Message
{
    /// <summary>
    /// Database identifier for the message.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Identifier of the parent that owns the message.
    /// </summary>
    public int ParentId { get; set; }

    /// <summary>
    /// The _id from the TalkingPoints API response.
    /// </summary>
    public string ExternalMessageId { get; set; } = string.Empty;

    /// <summary>
    /// Contact-specific message identifier from TalkingPoints.
    /// </summary>
    public string ContactMessageId { get; set; } = string.Empty;

    /// <summary>
    /// Student name associated with the message.
    /// </summary>
    public string StudentName { get; set; } = string.Empty;

    /// <summary>
    /// Sender display name from TalkingPoints.
    /// </summary>
    public string FromName { get; set; } = string.Empty;

    /// <summary>
    /// Raw message body text.
    /// </summary>
    public string MessageText { get; set; } = string.Empty;

    /// <summary>
    /// UTC instant the message was sent, taken from the TalkingPoints API (DisplayDate, falling back
    /// to CreatedAt). Falls back to the fetch time when the API supplies neither, so it is not
    /// guaranteed to be the true send time in that case.
    /// </summary>
    public DateTime SentAt { get; set; }

    /// <summary>
    /// UTC time when the message record was created locally.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Null until the message has been categorized and routed to news/newsletter.
    /// </summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// Parent that owns the message.
    /// </summary>
    public Parent Parent { get; set; } = null!;
}
