using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class AnthropicAiClientTests
{
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
        => new(new HttpClient(handler), Options.Create(options ?? DefaultOptions()));

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
                Content = new StringContent(
                    """{"content":[{"type":"text","text":"ok"}]}""",
                    Encoding.UTF8, "application/json")
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
                Content = new StringContent(
                    """{"content":[{"type":"text","text":"ok"}]}""",
                    Encoding.UTF8, "application/json")
            });

        var client = CreateClient(mock.Object);
        await client.CompleteAsync(new AiCompletionRequest("my prompt", "claude-sonnet-4-5-20250929", 8192));

        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("model").GetString().Should().Be("claude-sonnet-4-5-20250929");
        doc.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(8192);
        doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("my prompt");
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
