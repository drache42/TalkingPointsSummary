using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
        var requestBody = new
        {
            model = request.ModelId,
            max_tokens = request.MaxTokens,
            messages = new[] { new { role = "user", content = request.Prompt } }
        };

        var httpRequest = BuildRequest(provider, requestBody);
        var response = await _httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync(ct);
        var envelope = JsonSerializer.Deserialize<AnthropicEnvelope>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var text = envelope?.Content?.FirstOrDefault()?.Text ?? string.Empty;
        return new AiCompletionResult(text, raw);
    }

    /// <inheritdoc/>
    public async Task<AiCredentialCheckResult> ValidateCredentialsAsync(CancellationToken ct = default)
    {
        var provider = _options.Anthropic;
        var requestBody = new
        {
            model = _options.Profiles.Validation.ModelId,
            max_tokens = 1,
            messages = Array.Empty<object>()
        };

        var httpRequest = BuildRequest(provider, requestBody);

        try
        {
            var response = await _httpClient.SendAsync(httpRequest, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    new AiCredentialCheckResult(false, "API key rejected (401 Unauthorized)"),
                HttpStatusCode.Forbidden =>
                    new AiCredentialCheckResult(false, "API key forbidden (403 Forbidden)"),
                _ =>
                    new AiCredentialCheckResult(true, $"Credentials accepted (HTTP {(int)response.StatusCode})")
            };
        }
        catch (Exception ex)
        {
            return new AiCredentialCheckResult(false, $"Request failed: {ex.Message}");
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
        public List<AnthropicContentBlock>? Content { get; set; }
    }

    private sealed class AnthropicContentBlock
    {
        public string Type { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }
}
