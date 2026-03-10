using System.Text.Json.Serialization;

namespace TalkingPointsSummary.Services;

/// <summary>
/// JSON shape expected from the message categorization model response.
/// </summary>
public class CategorizationJsonResponse
{
    /// <summary>
    /// Message identifier echoed by the model.
    /// </summary>
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    /// <summary>
    /// Whether the model found a newsletter URL in the message.
    /// </summary>
    [JsonPropertyName("has_newsletter_url")]
    public bool? HasNewsletterUrl { get; set; }

    /// <summary>
    /// Newsletter URL extracted by the model.
    /// </summary>
    [JsonPropertyName("newsletter_url")]
    public string? NewsletterUrl { get; set; }

    /// <summary>
    /// Whether the model considers the message text itself to be news.
    /// </summary>
    [JsonPropertyName("is_news_itself")]
    public bool? IsNewsItself { get; set; }

    /// <summary>
    /// Short summary returned by the model.
    /// </summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
}
