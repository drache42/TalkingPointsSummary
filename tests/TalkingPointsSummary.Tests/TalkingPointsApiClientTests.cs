using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class TalkingPointsApiClientTests
{
    private readonly Parent _testParent = new()
    {
        Id = 1,
        Name = "Test Parent",
        TalkingPointsToken = "test-token-123",
        TalkingPointsContactId = "contact-456",
        EmailRecipients = "test@example.com"
    };

    private static TalkingPointsApiClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new TalkingPointsApiClient(httpClient, NullLogger<TalkingPointsApiClient>.Instance);
    }

    private static Mock<HttpMessageHandler> CreateMockHandler(HttpStatusCode statusCode, object? responseBody = null)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        var responseContent = responseBody != null
            ? new StringContent(JsonSerializer.Serialize(responseBody), System.Text.Encoding.UTF8, "application/json")
            : new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = responseContent
            });

        return mockHandler;
    }

    [Fact]
    public async Task FetchMessagesAsync_SendsCorrectUrlAndHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("""{"data":{"messages":[]}}""", System.Text.Encoding.UTF8, "application/json")
            });

        var client = CreateClient(mockHandler.Object);
        await client.FetchMessagesAsync(_testParent);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.ToString().Should().Contain("/api/parents/v3/messages/feed");
        capturedRequest.Headers.GetValues("x-token").Should().ContainSingle().Which.Should().Be("test-token-123");
        capturedRequest.Headers.GetValues("x-contactid").Should().ContainSingle().Which.Should().Be("contact-456");
        capturedRequest.Headers.GetValues("x-app-version").Should().ContainSingle().Which.Should().Be("5.0.0");
        capturedRequest.Headers.GetValues("x-language").Should().ContainSingle().Which.Should().Be("en");
        capturedRequest.Headers.GetValues("x-mobile-platform").Should().ContainSingle().Which.Should().Be("web");
    }

    [Fact]
    public async Task FetchMessagesAsync_SuccessfulResponse_DeserializesCorrectly()
    {
        var responseBody = new
        {
            data = new
            {
                messages = new[]
                {
                    new
                    {
                        _id = "msg-001",
                        contactMessageId = "contact-msg-001",
                        text = "Hello parents!",
                        fromName = "Ms. Smith",
                        from = new { user = new { signature = "Ms. Jane Smith" } },
                        contactInfo = new { studentName = "Clara" },
                        createdAt = "2026-03-01T10:00:00Z",
                        displayDate = "2026-03-01T10:30:00Z"
                    }
                }
            }
        };

        var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseBody);
        var client = CreateClient(mockHandler.Object);

        var result = await client.FetchMessagesAsync(_testParent);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("msg-001");
        result[0].Text.Should().Be("Hello parents!");
        result[0].ContactInfo!.StudentName.Should().Be("Clara");
        result[0].From!.User!.Signature.Should().Be("Ms. Jane Smith");
    }

    [Fact]
    public async Task FetchMessagesAsync_EmptyMessages_ReturnsEmptyList()
    {
        var responseBody = new { data = new { messages = Array.Empty<object>() } };
        var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseBody);
        var client = CreateClient(mockHandler.Object);

        var result = await client.FetchMessagesAsync(_testParent);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchMessagesAsync_NonSuccessStatus_Throws()
    {
        var mockHandler = CreateMockHandler(HttpStatusCode.Unauthorized);
        var client = CreateClient(mockHandler.Object);

        var act = () => client.FetchMessagesAsync(_testParent);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
