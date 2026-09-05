using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>
    /// Initializes an Anthropic AI client.
    /// </summary>
    /// <param name="httpClient">HTTP client used to call the Anthropic API.</param>
    /// <param name="options">AI configuration including provider credentials and profiles.</param>
    public AnthropicAiClient(HttpClient httpClient, IOptions<AiOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken ct = default)
    {
        var provider = _options.Anthropic;
        var requestBody = BuildCompletionBody(request);

        var httpRequest = BuildRequest(provider, requestBody);
        var response = await _httpClient.SendAsync(httpRequest, ct);

        if (!response.IsSuccessStatusCode)
        {
            // EnsureSuccessStatusCode() throws before the body is ever read, discarding the only
            // place the provider explains a 400 -- which parameter it rejected and why. That
            // reason is worth more than a generic status-code exception to whoever reads the log.
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Anthropic API request for model '{request.ModelId}' failed with "
                + $"{(int)response.StatusCode} {response.StatusCode}: {errorBody}");
        }

        var raw = await response.Content.ReadAsStringAsync(ct);
        var envelope = JsonSerializer.Deserialize<AnthropicEnvelope>(raw, ResponseSerializerOptions);

        var text = SelectTextBlock(envelope);
        return new AiCompletionResult(text, raw);
    }

    /// <summary>
    /// Builds the messages API request body, adding the thinking and effort parameters that the
    /// requested profile calls for.
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
            // Claude 4.5 and earlier: fixed thinking budget, and the effort parameter is not supported.
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
    }

    private sealed class AnthropicContentBlock
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
