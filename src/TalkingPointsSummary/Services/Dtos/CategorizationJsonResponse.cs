using System.Text.Json.Serialization;

namespace TalkingPointsSummary.Services;

public class CategorizationJsonResponse
{
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("has_newsletter_url")]
    public bool? HasNewsletterUrl { get; set; }

    [JsonPropertyName("newsletter_url")]
    public string? NewsletterUrl { get; set; }

    [JsonPropertyName("is_news_itself")]
    public bool? IsNewsItself { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
}
