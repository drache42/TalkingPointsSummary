using System.Text.Json.Serialization;

namespace TalkingPointsSummary.Services;

public class TalkingPointsMessage
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    public string? ContactMessageId { get; set; }
    public string? Text { get; set; }
    public string? FromName { get; set; }
    public TalkingPointsFrom? From { get; set; }
    public TalkingPointsContactInfo? ContactInfo { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? DisplayDate { get; set; }
}
