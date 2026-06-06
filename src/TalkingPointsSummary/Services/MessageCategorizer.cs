using System.Text.Json;
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
    /// <summary>
    /// Message identifier associated with the categorization result.
    /// </summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// Whether the message contains a newsletter URL.
    /// </summary>
    public bool HasNewsletterUrl { get; set; }

    /// <summary>
    /// Newsletter URL returned by the model, when present.
    /// </summary>
    public string? NewsletterUrl { get; set; }

    /// <summary>
    /// Whether the message itself should be treated as direct news.
    /// </summary>
    public bool IsNewsItself { get; set; }

    /// <summary>
    /// Short AI-generated summary of the message content.
    /// </summary>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// Uses the configured AI provider to categorize messages as newsletter links, direct news, or neither.
/// </summary>
public partial class MessageCategorizer : IMessageCategorizer
{
    private static readonly MessageCategorizationPromptBuilder PromptBuilder = new();

    private readonly IAiClient _aiClient;
    private readonly AiOptions _options;
    private readonly ILogger<MessageCategorizer> _logger;

    /// <summary>
    /// Initializes a message categorizer.
    /// </summary>
    /// <param name="aiClient">AI client used to send categorization requests.</param>
    /// <param name="aiOptions">AI configuration including the categorization profile.</param>
    /// <param name="logger">Logger used for categorization diagnostics.</param>
    public MessageCategorizer(
        IAiClient aiClient,
        IOptions<AiOptions> aiOptions,
        ILogger<MessageCategorizer> logger)
    {
        _aiClient = aiClient;
        _options = aiOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Categorizes a stored message using the AI provider and normalizes the response.
    /// </summary>
    /// <param name="message">Message to categorize.</param>
    /// <param name="ct">Token used to cancel the request.</param>
    public async Task<CategorizationResult> CategorizeAsync(Message message, CancellationToken ct = default)
    {
        _logger.LogInformation("Categorizing message {MessageId} from {FromName}",
            message.ExternalMessageId, message.FromName);

        var prompt = PromptBuilder.Build(message);
        var profile = _options.Profiles.Categorization;

        var aiResult = await _aiClient.CompleteAsync(
            new AiCompletionRequest(prompt, profile.ModelId, profile.MaxTokens), ct);

        var text = StripCodeFences().Replace(aiResult.Text, "").Trim();

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

    [GeneratedRegex(@"```json|```")]
    private static partial Regex StripCodeFences();
}
