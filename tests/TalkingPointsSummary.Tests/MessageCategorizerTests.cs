using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class MessageCategorizerTests
{
    private readonly Message _testMessage = new()
    {
        Id = 1,
        ParentId = 1,
        ExternalMessageId = "msg-001",
        FromName = "Ms. Smith",
        StudentName = "Clara",
        MessageText = "Here is the newsletter",
        SentAt = new DateTime(2026, 3, 7, 10, 0, 0, DateTimeKind.Utc)
    };

    private static MessageCategorizer CreateCategorizer(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new AnthropicOptions { ApiKey = "test-key" });
        return new MessageCategorizer(httpClient, options, NullLogger<MessageCategorizer>.Instance);
    }

    private static Mock<HttpMessageHandler> CreateMockHandler(string responseJson)
    {
        var anthropicResponse = new
        {
            content = new[] { new { type = "text", text = responseJson } }
        };

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(
                    JsonSerializer.Serialize(anthropicResponse),
                    System.Text.Encoding.UTF8, "application/json")
            });

        return mockHandler;
    }

    [Fact]
    public async Task CategorizeAsync_SuccessfulResponse_ReturnsCorrectResult()
    {
        var json = """
            {
              "message_id": "msg-001",
              "has_newsletter_url": true,
              "newsletter_url": "https://www.smore.com/abc",
              "is_news_itself": false,
              "summary": "Weekly newsletter link"
            }
            """;

        var mockHandler = CreateMockHandler(json);
        var categorizer = CreateCategorizer(mockHandler.Object);

        var result = await categorizer.CategorizeAsync(_testMessage);

        result.MessageId.Should().Be("msg-001");
        result.HasNewsletterUrl.Should().BeTrue();
        result.NewsletterUrl.Should().Be("https://www.smore.com/abc");
        result.IsNewsItself.Should().BeFalse();
        result.Summary.Should().Be("Weekly newsletter link");
    }

    [Fact]
    public async Task CategorizeAsync_JsonWrappedInCodeFences_StripsAndParses()
    {
        var json = """
            ```json
            {
              "message_id": "msg-001",
              "has_newsletter_url": false,
              "is_news_itself": true,
              "summary": "Picture day"
            }
            ```
            """;

        var mockHandler = CreateMockHandler(json);
        var categorizer = CreateCategorizer(mockHandler.Object);

        var result = await categorizer.CategorizeAsync(_testMessage);

        result.IsNewsItself.Should().BeTrue();
        result.Summary.Should().Be("Picture day");
    }

    [Fact]
    public async Task CategorizeAsync_MalformedJson_ReturnsFallbackResult()
    {
        var mockHandler = CreateMockHandler("not valid json at all {{{");
        var categorizer = CreateCategorizer(mockHandler.Object);

        var result = await categorizer.CategorizeAsync(_testMessage);

        result.IsNewsItself.Should().BeTrue();
        result.HasNewsletterUrl.Should().BeFalse();
        result.Summary.Should().Be("Unable to categorize");
    }
}
