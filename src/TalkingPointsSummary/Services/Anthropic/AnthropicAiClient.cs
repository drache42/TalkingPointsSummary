using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Anthropic implementation of <see cref="IAiClient"/>.
/// Handles request envelope construction, header injection, and response deserialization
/// for the Anthropic messages API.
/// </summary>
internal sealed class AnthropicAiClient : IAiClient
{
    /// <summary>
    /// Content block type carrying the model's visible answer. Extended thinking responses
    /// place a "thinking" block first, so the answer must be selected by type rather than position.
    /// </summary>
    private const string TextBlockType = "text";

    private static readonly JsonSerializerOptions ResponseSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly AiOptions _options;
    private readonly ILogger<AnthropicAiClient> _logger;

    /// <summary>
    /// Initializes an Anthropic AI client.
    /// </summary>
    /// <param name="httpClient">HTTP client used to call the Anthropic API.</param>
    /// <param name="options">AI configuration including provider credentials and profiles.</param>
    /// <param name="logger">Logger used to record stop reasons and token usage.</param>
    public AnthropicAiClient(HttpClient httpClient, IOptions<AiOptions> options, ILogger<AnthropicAiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken ct = default)
    {
        var provider = _options.Anthropic;
        var requestBody = BuildCompletionBody(request);

        var httpRequest = BuildRequest(provider, requestBody);
        var response = await _httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync(ct);
        var envelope = JsonSerializer.Deserialize<AnthropicEnvelope>(raw, ResponseSerializerOptions);

        var text = SelectTextBlock(envelope);
        var stopReason = envelope?.StopReason;
        var usage = MapUsage(envelope?.Usage);

        _logger.LogInformation(
            "Anthropic completion for model {ModelId}: stopReason={StopReason}, inputTokens={InputTokens}, " +
            "outputTokens={OutputTokens}, thinkingTokens={ThinkingTokens}, " +
            "cacheCreationInputTokens={CacheCreationInputTokens}, cacheReadInputTokens={CacheReadInputTokens}",
            request.ModelId,
            stopReason ?? "(none)",
            usage?.InputTokens,
            usage?.OutputTokens,
            usage?.ThinkingTokens,
            usage?.CacheCreationInputTokens,
            usage?.CacheReadInputTokens);

        if (AiResponseTruncatedException.IsTruncated(stopReason))
        {
            // Reported, not enforced. Whether a truncated answer is usable is the caller's decision:
            // a truncated digest must never be sent, while a truncated categorization still has a
            // safe fallback and would otherwise be retried and re-billed on every run forever.
            _logger.LogWarning(
                "Anthropic response for model {ModelId} was truncated at the max_tokens limit of {MaxTokens}; " +
                "returning the partial text for the caller to accept or reject.",
                request.ModelId, request.MaxTokens);
        }
        else if (AiResponseRefusedException.IsRefusal(stopReason))
        {
            // Also reported rather than enforced, for the same reason. The text block of a refusal
            // is prose about why the model declined, so it is never the answer that was asked for,
            // but a categorization can fall back to its default while a digest cannot be sent.
            _logger.LogWarning(
                "Anthropic response for model {ModelId} stopped with stop_reason '{StopReason}'; " +
                "the content is a refusal, not an answer.",
                request.ModelId, stopReason);
        }

        return new AiCompletionResult(text, raw, stopReason, usage);
    }

    /// <inheritdoc/>
    public async Task<AiCredentialCheckResult> ValidateCredentialsAsync(CancellationToken ct = default)
    {
        var provider = _options.Anthropic;
        var requestBody = new
        {
            model = _options.Profiles.Validation.ModelId,
            max_tokens = 1,
            // Intentionally empty: Anthropic validates the API key before the request body,
            // so a valid key returns 400 (bad request) rather than 200, which is the expected probe response.
            messages = Array.Empty<object>()
        };

        var httpRequest = BuildRequest(provider, requestBody);

        try
        {
            var response = await _httpClient.SendAsync(httpRequest, ct);
            var status = (int)response.StatusCode;
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    new AiCredentialCheckResult(false, false, "API key rejected (401 Unauthorized)"),
                HttpStatusCode.Forbidden =>
                    new AiCredentialCheckResult(false, false, "API key forbidden (403 Forbidden)"),
                HttpStatusCode.TooManyRequests =>
                    new AiCredentialCheckResult(false, true, $"Rate limited or quota exceeded (429) -- key validity inconclusive"),
                _ when status >= 500 =>
                    new AiCredentialCheckResult(false, true, $"Server error (HTTP {status}) -- key validity inconclusive"),
                _ =>
                    new AiCredentialCheckResult(true, false, $"Credentials accepted (HTTP {status})")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AiCredentialCheckResult(false, true, $"Probe failed, key validity inconclusive: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the messages API request body, adding the thinking, effort, and cached system prompt
    /// parameters that the requested profile calls for.
    /// </summary>
    /// <param name="request">Completion request carrying the profile values.</param>
    /// <returns>An object graph ready for JSON serialization.</returns>
    private static object BuildCompletionBody(AiCompletionRequest request)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = new[] { new { role = "user", content = request.Prompt } }
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            // Sent as a content-block array so cache_control can mark the system prompt as an
            // ephemeral cache breakpoint, letting the draft/critique/revise loop reuse it within a run.
            // Prefixes below the provider's minimum cacheable size simply do not cache, which is not an error.
            body["system"] = new[]
            {
                new
                {
                    type = TextBlockType,
                    text = request.SystemPrompt,
                    cache_control = new { type = "ephemeral" }
                }
            };
        }

        if (string.Equals(request.Thinking, AiThinkingModes.Adaptive, StringComparison.OrdinalIgnoreCase))
        {
            // Claude 5 family: adaptive thinking plus an optional reasoning effort level.
            body["thinking"] = new { type = "adaptive" };

            if (!string.IsNullOrWhiteSpace(request.Effort))
            {
                body["output_config"] = new { effort = request.Effort };
            }
        }
        else if (string.Equals(request.Thinking, AiThinkingModes.Budget, StringComparison.OrdinalIgnoreCase))
        {
            // Claude Haiku 4.5: fixed thinking budget, and the effort parameter is not supported.
            body["thinking"] = new { type = "enabled", budget_tokens = request.ThinkingBudgetTokens };
        }

        return body;
    }

    /// <summary>
    /// Returns the text of the first content block whose type is "text".
    /// </summary>
    /// <param name="envelope">Deserialized response envelope, possibly null.</param>
    /// <returns>The model's visible answer, or an empty string when no text block is present.</returns>
    private static string SelectTextBlock(AnthropicEnvelope? envelope)
    {
        if (envelope?.Content is null)
        {
            return string.Empty;
        }

        foreach (var block in envelope.Content)
        {
            if (block is not null
                && string.Equals(block.Type, TextBlockType, StringComparison.OrdinalIgnoreCase))
            {
                return block.Text ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Converts the provider usage block into the provider-agnostic token usage record.
    /// </summary>
    /// <param name="usage">Usage block from the response, possibly null.</param>
    /// <returns>Token usage, or null when the provider reported none.</returns>
    private static AiTokenUsage? MapUsage(AnthropicUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new AiTokenUsage(
            usage.InputTokens,
            usage.OutputTokens,
            usage.OutputTokensDetails?.ThinkingTokens,
            usage.CacheCreationInputTokens,
            usage.CacheReadInputTokens);
    }

    private static HttpRequestMessage BuildRequest(AnthropicProviderOptions provider, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            provider.BaseUrl.TrimEnd('/') + "/v1/messages");
        request.Headers.Add("x-api-key", provider.ApiKey);
        request.Headers.Add("anthropic-version", provider.ApiVersion);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed class AnthropicEnvelope
    {
        [JsonPropertyName("content")]
        public List<AnthropicContentBlock?>? Content { get; set; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; set; }

        [JsonPropertyName("usage")]
        public AnthropicUsage? Usage { get; set; }
    }

    private sealed class AnthropicContentBlock
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class AnthropicUsage
    {
        [JsonPropertyName("input_tokens")]
        public int? InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int? OutputTokens { get; set; }

        [JsonPropertyName("output_tokens_details")]
        public AnthropicOutputTokensDetails? OutputTokensDetails { get; set; }

        [JsonPropertyName("cache_creation_input_tokens")]
        public int? CacheCreationInputTokens { get; set; }

        [JsonPropertyName("cache_read_input_tokens")]
        public int? CacheReadInputTokens { get; set; }
    }

    private sealed class AnthropicOutputTokensDetails
    {
        [JsonPropertyName("thinking_tokens")]
        public int? ThinkingTokens { get; set; }
    }
}

/// <summary>
/// Exception thrown by a call site that cannot use a partial answer after the provider stopped
/// generating at the max_tokens limit. The client itself only reports the stop reason, because
/// truncation is fatal for a digest that would otherwise be emailed but merely degraded for a
/// categorization that has a fallback.
/// </summary>
public sealed class AiResponseTruncatedException : Exception
{
    /// <summary>
    /// Stop reason the provider reports when generation hit the max_tokens ceiling.
    /// </summary>
    public const string MaxTokensStopReason = "max_tokens";

    /// <summary>
    /// Initializes a new truncation exception.
    /// </summary>
    /// <param name="message">Explanation of which model was truncated and at what limit.</param>
    public AiResponseTruncatedException(string message) : base(message)
    {
    }

    /// <summary>
    /// Reports whether a provider stop reason means the response was cut off at the token limit.
    /// </summary>
    /// <param name="stopReason">Stop reason from <see cref="AiCompletionResult.StopReason"/>.</param>
    /// <returns><c>true</c> when the response is truncated.</returns>
    public static bool IsTruncated(string? stopReason)
        => string.Equals(stopReason, MaxTokensStopReason, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Exception thrown by a call site that cannot use a model refusal in place of the answer it
/// asked for. Like truncation, the client only reports the stop reason: a refused categorization
/// falls back safely, while a refused digest is a paragraph of prose that must never be emailed
/// as the week's news.
/// </summary>
/// <remarks>
/// A refusal arrives as HTTP 200 with a normal-looking text block, so nothing downstream can tell
/// it apart from a digest by inspecting the text. The stop reason is the only signal there is.
/// </remarks>
public sealed class AiResponseRefusedException : Exception
{
    /// <summary>
    /// Stop reason the provider reports when the model declined to answer.
    /// </summary>
    public const string RefusalStopReason = "refusal";

    /// <summary>
    /// Initializes a new refusal exception.
    /// </summary>
    /// <param name="message">Explanation of which model refused and what was being generated.</param>
    public AiResponseRefusedException(string message) : base(message)
    {
    }

    /// <summary>
    /// Reports whether a provider stop reason means the model declined to answer.
    /// </summary>
    /// <param name="stopReason">Stop reason from <see cref="AiCompletionResult.StopReason"/>.</param>
    /// <returns><c>true</c> when the response is a refusal.</returns>
    public static bool IsRefusal(string? stopReason)
        => string.Equals(stopReason, RefusalStopReason, StringComparison.OrdinalIgnoreCase);
}
