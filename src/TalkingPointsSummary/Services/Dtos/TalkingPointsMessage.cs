using System.Text.Json.Serialization;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Message payload returned by the TalkingPoints feed API.
/// </summary>
public class TalkingPointsMessage
{
    /// <summary>
    /// External identifier for the message.
    /// </summary>
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Contact-specific message identifier.
    /// </summary>
    public string? ContactMessageId { get; set; }

    /// <summary>
    /// Raw message body text.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Sender display name returned by the API.
    /// </summary>
    public string? FromName { get; set; }

    /// <summary>
    /// Sender details payload.
    /// </summary>
    public TalkingPointsFrom? From { get; set; }

    /// <summary>
    /// Contact information payload.
    /// </summary>
    public TalkingPointsContactInfo? ContactInfo { get; set; }

    /// <summary>
    /// Creation timestamp supplied by the API.
    /// </summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// Display date supplied by the API.
    /// </summary>
    public DateTime? DisplayDate { get; set; }
}
