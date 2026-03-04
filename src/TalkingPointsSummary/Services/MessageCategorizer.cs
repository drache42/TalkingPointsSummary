using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Result of AI message categorization.
/// </summary>
public class CategorizationResult
{
    public string MessageId { get; set; } = string.Empty;
    public bool HasNewsletterUrl { get; set; }
    public string? NewsletterUrl { get; set; }
    public bool IsNewsItself { get; set; }
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// Uses Claude Haiku to categorize messages as newsletter links, direct news, or neither.
/// </summary>
public partial class MessageCategorizer
{
    private readonly HttpClient _httpClient;
    private readonly AppSettings _settings;
    private readonly ILogger<MessageCategorizer> _logger;

    public MessageCategorizer(
        HttpClient httpClient,
        IOptions<AppSettings> settings,
        ILogger<MessageCategorizer> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<CategorizationResult> CategorizeAsync(Message message, CancellationToken ct = default)
    {
        _logger.LogInformation("Categorizing message {MessageId} from {FromName}",
            message.ExternalMessageId, message.FromName);

        var prompt = BuildPrompt(message);

        var requestBody = new
        {
            model = "claude-haiku-4-5-20251001",
            max_tokens = 1024,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", _settings.AnthropicApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(requestBody);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var apiResponse = await response.Content.ReadFromJsonAsync<AnthropicResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

        var text = apiResponse?.Content?.FirstOrDefault()?.Text ?? "{}";

        // Strip markdown code fences if present
        text = StripCodeFences().Replace(text, "").Trim();

        try
        {
            var result = JsonSerializer.Deserialize<CategorizationJsonResponse>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new CategorizationResult
            {
                MessageId = result?.MessageId ?? message.ExternalMessageId,
                HasNewsletterUrl = result?.HasNewsletterUrl ?? false,
                NewsletterUrl = result?.NewsletterUrl,
                IsNewsItself = result?.IsNewsItself ?? false,
                Summary = result?.Summary ?? string.Empty
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse AI categorization response for message {MessageId}. Raw: {Text}",
                message.ExternalMessageId, text);

            return new CategorizationResult
            {
                MessageId = message.ExternalMessageId,
                HasNewsletterUrl = false,
                IsNewsItself = true, // Default to treating as news
                Summary = "Unable to categorize"
            };
        }
    }

    private static string BuildPrompt(Message message)
    {
        return $$"""
            You are analyzing school messages to categorize them. Respond ONLY with valid JSON.

            Analyze this message and determine:
            1. Does it contain a newsletter URL? (look for links to school newsletters, typically smore.com or similar)
            2. Is the message itself important news? (announcements, events, deadlines, holidays, school updates)

            Message from: {{message.FromName}}
            Date: {{message.SentAt:O}}
            Text: {{message.MessageText}}
            MessageID: {{message.ExternalMessageId}}

            Respond with this exact JSON structure:
            {
              "message_id": "MessageId that was passed in",
              "has_newsletter_url": true or false,
              "newsletter_url": "the URL if found, otherwise null",
              "is_news_itself": true or false,
              "summary": "brief 1-sentence description of what this is"
            }

            Rules:
            * `has_newsletter_url`: set to true if the message contains a URL to a newsletter (e.g. smore.com or similar). Extract the URL into newsletter_url.
            * `is_news_itself`: set to true if the message body contains actual news, announcements, events, deadlines, reminders, or school updates. These two fields are independent — a message can have both a newsletter link AND be news itself.
            * Assume messages are news by default. Only set `is_news_itself` to false if the message is purely a newsletter link with little or no additional content (e.g. "Here is the newsletter: [url]" or "Weekly news [url]").
            * `summary` should always be a brief 1-sentence description of the message content, regardless of classification.
            """;
    }

    [GeneratedRegex(@"```json|```")]
    private static partial Regex StripCodeFences();
}

// --- Anthropic API DTOs ---

public class AnthropicResponse
{
    public List<AnthropicContent>? Content { get; set; }
}

public class AnthropicContent
{
    public string Type { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

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
