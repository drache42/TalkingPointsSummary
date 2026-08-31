using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class AnthropicAiClientTests
{
    private const string TextOnlyResponse = """{"content":[{"type":"text","text":"ok"}]}""";

    private static AiOptions DefaultOptions(string apiKey = "test-key") => new()
    {
        Provider = "Anthropic",
        Anthropic = new AnthropicProviderOptions
        {
            ApiKey = apiKey,
            BaseUrl = "https://api.anthropic.com",
            ApiVersion = "2023-06-01"
        },
        Profiles = new AiProfilesOptions
        {
            Validation = new AiProfileOptions { ModelId = "claude-haiku-3-5-20241022", MaxTokens = 1 }
        }
    };

    private static AnthropicAiClient CreateClient(HttpMessageHandler handler, AiOptions? options = null)
        => new(new HttpClient(handler),
            Options.Create(options ?? DefaultOptions()),
            NullLogger<AnthropicAiClient>.Instance);

    private static Mock<HttpMessageHandler> CreateMockHandler(HttpStatusCode statusCode, string responseBody)
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        return mock;
    }

    /// <summary>
    /// Runs a completion against a stubbed handler and returns the JSON body that was actually sent.
    /// </summary>
    private static async Task<JsonElement> CaptureRequestBodyAsync(
        AiCompletionRequest request,
        string responseBody = TextOnlyResponse)
    {
        HttpRequestMessage? capturedRequest = null;
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });

        var client = CreateClient(mock.Object);
        await client.CompleteAsync(request);

        capturedRequest.Should().NotBeNull();
        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task CompleteAsync_HappyPath_ExtractsTextFromResponse()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK,
            """{"content":[{"type":"text","text":"Hello world"}]}""");
        var client = CreateClient(handler.Object);

        var result = await client.CompleteAsync(
            new AiCompletionRequest("test prompt", "claude-haiku-4-5-20251001", 100));

        result.Text.Should().Be("Hello world");
        result.RawResponse.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CompleteAsync_NullContentArray_ReturnsEmptyText()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, """{"content":null}""");
        var client = CreateClient(handler.Object);

        var result = await client.CompleteAsync(
            new AiCompletionRequest("prompt", "model", 100));

        result.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteAsync_SetsXApiKeyAndVersionHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(TextOnlyResponse, Encoding.UTF8, "application/json")
            });

        var client = CreateClient(mock.Object);
        await client.CompleteAsync(new AiCompletionRequest("prompt", "model", 100));

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.GetValues("x-api-key").Should().Contain("test-key");
        capturedRequest.Headers.GetValues("anthropic-version").Should().Contain("2023-06-01");
    }

    [Fact]
    public async Task CompleteAsync_SendsModelAndMaxTokensInBody()
    {
        var root = await CaptureRequestBodyAsync(
            new AiCompletionRequest("my prompt", "claude-sonnet-4-5-20250929", 8192));

        root.GetProperty("model").GetString().Should().Be("claude-sonnet-4-5-20250929");
        root.GetProperty("max_tokens").GetInt32().Should().Be(8192);
        root.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("user");
        root.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("my prompt");
    }

    [Fact]
    public async Task CompleteAsync_AdaptiveThinkingWithEffort_SendsAdaptiveThinkingAndOutputConfig()
    {
        var root = await CaptureRequestBodyAsync(new AiCompletionRequest(
            "prompt",
            "claude-sonnet-5",
            32000,
            Thinking: AiThinkingModes.Adaptive,
            Effort: AiEffortLevels.High));

        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("adaptive");
        root.GetProperty("thinking").TryGetProperty("budget_tokens", out _).Should().BeFalse();
        root.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("high");
    }

    [Fact]
    public async Task CompleteAsync_AdaptiveThinkingWithoutEffort_OmitsOutputConfig()
    {
        var root = await CaptureRequestBodyAsync(new AiCompletionRequest(
            "prompt",
            "claude-opus-5",
            32000,
            Thinking: AiThinkingModes.Adaptive,
            Effort: null));

        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("adaptive");
        root.TryGetProperty("output_config", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_BudgetThinking_SendsEnabledThinkingWithBudgetTokens()
    {
        var root = await CaptureRequestBodyAsync(new AiCompletionRequest(
            "prompt",
            "claude-haiku-4-5-20251001",
            4096,
            Thinking: AiThinkingModes.Budget,
            ThinkingBudgetTokens: 2048));

        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        root.GetProperty("thinking").GetProperty("budget_tokens").GetInt32().Should().Be(2048);
    }

    [Fact]
    public async Task CompleteAsync_BudgetThinking_NeverSendsEffortEvenWhenProfileSetsIt()
    {
        // Haiku 4.5 returns HTTP 400 when effort accompanies budget thinking.
        var root = await CaptureRequestBodyAsync(new AiCompletionRequest(
            "prompt",
            "claude-haiku-4-5-20251001",
            4096,
            Thinking: AiThinkingModes.Budget,
            ThinkingBudgetTokens: 2048,
            Effort: AiEffortLevels.High));

        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
        root.TryGetProperty("output_config", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_ThinkingNone_OmitsThinkingAndOutputConfig()
    {
        var root = await CaptureRequestBodyAsync(new AiCompletionRequest(
            "prompt",
            "claude-haiku-4-5-20251001",
            1024,
            Thinking: AiThinkingModes.None,
            Effort: AiEffortLevels.High));

        root.TryGetProperty("thinking", out _).Should().BeFalse();
        root.TryGetProperty("output_config", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_DefaultRequest_OmitsThinkingOutputConfigAndSystem()
    {
        var root = await CaptureRequestBodyAsync(new AiCompletionRequest("prompt", "model", 512));

        root.TryGetProperty("thinking", out _).Should().BeFalse();
        root.TryGetProperty("output_config", out _).Should().BeFalse();
        root.TryGetProperty("system", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_SystemPrompt_SendsContentBlockArrayWithEphemeralCacheControl()
    {
        var root = await CaptureRequestBodyAsync(new AiCompletionRequest(
            "prompt",
            "claude-sonnet-5",
            32000,
            SystemPrompt: "You are an editor."));

        var system = root.GetProperty("system");
        system.ValueKind.Should().Be(JsonValueKind.Array);
        system.GetArrayLength().Should().Be(1);
        system[0].GetProperty("type").GetString().Should().Be("text");
        system[0].GetProperty("text").GetString().Should().Be("You are an editor.");
        system[0].GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
    }

    [Fact]
    public async Task CompleteAsync_ShortSystemPrompt_StillSendsCacheControlWithoutError()
    {
        // Prefixes below the provider's minimum cacheable size simply do not cache; that is not an error.
        var root = await CaptureRequestBodyAsync(new AiCompletionRequest(
            "prompt",
            "claude-sonnet-5",
            32000,
            SystemPrompt: "Be brief."));

        root.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString()
            .Should().Be("ephemeral");
    }

    [Theory]
    [InlineData((string?)null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompleteAsync_BlankSystemPrompt_OmitsSystemParameter(string? systemPrompt)
    {
        var root = await CaptureRequestBodyAsync(new AiCompletionRequest(
            "prompt", "model", 512, SystemPrompt: systemPrompt));

        root.TryGetProperty("system", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_LeadingThinkingBlock_SelectsFirstTextBlock()
    {
        // Live claude-opus-5 responses lead with a thinking block that carries no text.
        var handler = CreateMockHandler(HttpStatusCode.OK,
            """
            {"content":[
              {"type":"thinking","thinking":"Let me consider the digest."},
              {"type":"text","text":"# Weekly digest"},
              {"type":"text","text":"trailing block"}
            ],"stop_reason":"end_turn"}
            """);
        var client = CreateClient(handler.Object);

        var result = await client.CompleteAsync(new AiCompletionRequest("prompt", "claude-opus-5", 32000));

        result.Text.Should().Be("# Weekly digest");
    }

    [Fact]
    public async Task CompleteAsync_ThinkingBlockWithEmptyTextProperty_StillSelectsTextBlock()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK,
            """{"content":[{"type":"thinking","text":""},{"type":"text","text":"real answer"}]}""");
        var client = CreateClient(handler.Object);

        var result = await client.CompleteAsync(new AiCompletionRequest("prompt", "claude-opus-5", 32000));

        result.Text.Should().Be("real answer");
    }

    [Fact]
    public async Task CompleteAsync_NoTextBlockInContent_ReturnsEmptyText()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK,
            """{"content":[{"type":"thinking","thinking":"only thinking"}],"stop_reason":"end_turn"}""");
        var client = CreateClient(handler.Object);

        var result = await client.CompleteAsync(new AiCompletionRequest("prompt", "claude-opus-5", 32000));

        result.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteAsync_ParsesStopReasonAndFullUsageBlock()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK,
            """
            {"content":[{"type":"text","text":"digest"}],
             "stop_reason":"end_turn",
             "usage":{"input_tokens":1234,"output_tokens":567,
                      "output_tokens_details":{"thinking_tokens":321},
                      "cache_creation_input_tokens":890,
                      "cache_read_input_tokens":432}}
            """);
        var client = CreateClient(handler.Object);

        var result = await client.CompleteAsync(new AiCompletionRequest("prompt", "claude-sonnet-5", 32000));

        result.StopReason.Should().Be("end_turn");
        result.Usage.Should().NotBeNull();
        result.Usage!.InputTokens.Should().Be(1234);
        result.Usage.OutputTokens.Should().Be(567);
        result.Usage.ThinkingTokens.Should().Be(321);
        result.Usage.CacheCreationInputTokens.Should().Be(890);
        result.Usage.CacheReadInputTokens.Should().Be(432);
    }

    [Fact]
    public async Task CompleteAsync_UsageWithoutThinkingDetails_LeavesThinkingTokensNull()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK,
            """
            {"content":[{"type":"text","text":"ok"}],
             "stop_reason":"end_turn",
             "usage":{"input_tokens":10,"output_tokens":20}}
            """);
        var client = CreateClient(handler.Object);

        var result = await client.CompleteAsync(new AiCompletionRequest("prompt", "model", 512));

        result.Usage.Should().NotBeNull();
        result.Usage!.InputTokens.Should().Be(10);
        result.Usage.OutputTokens.Should().Be(20);
        result.Usage.ThinkingTokens.Should().BeNull();
        result.Usage.CacheCreationInputTokens.Should().BeNull();
        result.Usage.CacheReadInputTokens.Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_ResponseWithoutUsage_ReturnsNullUsageAndStopReason()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK, TextOnlyResponse);
        var client = CreateClient(handler.Object);

        var result = await client.CompleteAsync(new AiCompletionRequest("prompt", "model", 512));

        result.Usage.Should().BeNull();
        result.StopReason.Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_MaxTokensStopReason_ReturnsPartialTextAndStopReasonWithoutThrowing()
    {
        // Truncation is reported, not enforced: whether a partial answer is usable belongs to the
        // caller. The digest path refuses it, while a categorization falls back on the partial text
        // rather than failing the message and re-sending it to the model on every run forever.
        var handler = CreateMockHandler(HttpStatusCode.OK,
            """
            {"content":[{"type":"text","text":"{\"has_newsletter_url\": fal"}],
             "stop_reason":"max_tokens",
             "usage":{"input_tokens":100,"output_tokens":1024}}
            """);
        var client = CreateClient(handler.Object);

        var result = await client.CompleteAsync(
            new AiCompletionRequest("prompt", "claude-haiku-4-5-20251001", 1024));

        result.Text.Should().Be("{\"has_newsletter_url\": fal");
        result.StopReason.Should().Be("max_tokens");
        result.Usage!.OutputTokens.Should().Be(1024);
    }

    [Theory]
    [InlineData("max_tokens", true)]
    [InlineData("MAX_TOKENS", true)]
    [InlineData("end_turn", false)]
    [InlineData("stop_sequence", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTruncated_RecognizesTheMaxTokensStopReasonRegardlessOfCase(string? stopReason, bool expected)
    {
        AiResponseTruncatedException.IsTruncated(stopReason).Should().Be(expected);
    }

    [Fact]
    public async Task CompleteAsync_NonTruncatingStopReason_ReturnsResult()
    {
        var handler = CreateMockHandler(HttpStatusCode.OK,
            """{"content":[{"type":"text","text":"complete"}],"stop_reason":"stop_sequence"}""");
        var client = CreateClient(handler.Object);

        var result = await client.CompleteAsync(new AiCompletionRequest("prompt", "model", 512));

        result.Text.Should().Be("complete");
        result.StopReason.Should().Be("stop_sequence");
    }

    [Fact]
    public async Task CompleteAsync_NonSuccessStatus_Throws()
    {
        var handler = CreateMockHandler(HttpStatusCode.BadRequest, """{"error":"bad request"}""");
        var client = CreateClient(handler.Object);

        var act = async () => await client.CompleteAsync(new AiCompletionRequest("prompt", "model", 512));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ValidateCredentialsAsync_SendsEmptyMessagesProbe()
    {
        HttpRequestMessage? capturedRequest = null;
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.BadRequest,
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var client = CreateClient(mock.Object);
        var result = await client.ValidateCredentialsAsync();

        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;
        root.GetProperty("model").GetString().Should().Be("claude-haiku-3-5-20241022");
        root.GetProperty("max_tokens").GetInt32().Should().Be(1);
        root.GetProperty("messages").GetArrayLength().Should().Be(0);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateCredentialsAsync_UnauthorizedStatus_ReturnsInvalid()
    {
        var handler = CreateMockHandler(HttpStatusCode.Unauthorized, "{}");
        var client = CreateClient(handler.Object);

        var result = await client.ValidateCredentialsAsync();

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Contain("401");
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ForbiddenStatus_ReturnsInvalid()
    {
        var handler = CreateMockHandler(HttpStatusCode.Forbidden, "{}");
        var client = CreateClient(handler.Object);

        var result = await client.ValidateCredentialsAsync();

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Contain("403");
    }

    [Fact]
    public async Task ValidateCredentialsAsync_OtherStatus_ReturnsValid()
    {
        // 400 Bad Request means auth passed (invalid request body is acceptable for a probe)
        var handler = CreateMockHandler(HttpStatusCode.BadRequest, "{}");
        var client = CreateClient(handler.Object);

        var result = await client.ValidateCredentialsAsync();

        result.IsValid.Should().BeTrue();
        result.Reason.Should().Contain("400");
    }

    [Fact]
    public async Task ValidateCredentialsAsync_NetworkException_ReturnsInconclusive()
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var client = CreateClient(mock.Object);

        var result = await client.ValidateCredentialsAsync();

        result.IsValid.Should().BeFalse();
        result.IsInconclusive.Should().BeTrue();
        result.Reason.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task ValidateCredentialsAsync_TooManyRequests_ReturnsInconclusive()
    {
        var handler = CreateMockHandler(HttpStatusCode.TooManyRequests, "{}");
        var client = CreateClient(handler.Object);

        var result = await client.ValidateCredentialsAsync();

        result.IsValid.Should().BeFalse();
        result.IsInconclusive.Should().BeTrue();
        result.Reason.Should().Contain("429");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ValidateCredentialsAsync_ServerError_ReturnsInconclusive(HttpStatusCode statusCode)
    {
        var handler = CreateMockHandler(statusCode, "{}");
        var client = CreateClient(handler.Object);

        var result = await client.ValidateCredentialsAsync();

        result.IsValid.Should().BeFalse();
        result.IsInconclusive.Should().BeTrue();
        result.Reason.Should().Contain(((int)statusCode).ToString());
    }
}
