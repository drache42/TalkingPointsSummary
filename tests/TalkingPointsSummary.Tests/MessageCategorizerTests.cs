using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
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
        StudentName = "StudentOne",
        MessageText = "Here is the newsletter",
        SentAt = new DateTime(2026, 3, 7, 10, 0, 0, DateTimeKind.Utc)
    };

    private static MessageCategorizer CreateCategorizer(Mock<IAiClient> mockAiClient)
    {
        var options = Options.Create(new AiOptions
        {
            Provider = "Anthropic",
            Profiles = new AiProfilesOptions
            {
                Categorization = new AiProfileOptions { ModelId = "claude-haiku-4-5-20251001", MaxTokens = 1024 }
            }
        });
        return new MessageCategorizer(mockAiClient.Object, options, NullLogger<MessageCategorizer>.Instance);
    }

    private static Mock<IAiClient> CreateMockAiClient(string responseText)
    {
        var mock = new Mock<IAiClient>();
        mock.Setup(c => c.CompleteAsync(It.IsAny<AiCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiCompletionResult(responseText));
        return mock;
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

        var mock = CreateMockAiClient(json);
        var categorizer = CreateCategorizer(mock);

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

        var mock = CreateMockAiClient(json);
        var categorizer = CreateCategorizer(mock);

        var result = await categorizer.CategorizeAsync(_testMessage);

        result.IsNewsItself.Should().BeTrue();
        result.Summary.Should().Be("Picture day");
    }

    [Fact]
    public async Task CategorizeAsync_MalformedJson_ReturnsFallbackResult()
    {
        var mock = CreateMockAiClient("not valid json at all {{{");
        var categorizer = CreateCategorizer(mock);

        var result = await categorizer.CategorizeAsync(_testMessage);

        result.IsNewsItself.Should().BeTrue();
        result.HasNewsletterUrl.Should().BeFalse();
        result.Summary.Should().Be("Unable to categorize");
    }

    [Fact]
    public async Task CategorizeAsync_UsesCategorizationProfile()
    {
        var mock = CreateMockAiClient("{}");
        var categorizer = CreateCategorizer(mock);

        await categorizer.CategorizeAsync(_testMessage);

        mock.Verify(c => c.CompleteAsync(
            It.Is<AiCompletionRequest>(r =>
                r.ModelId == "claude-haiku-4-5-20251001" &&
                r.MaxTokens == 1024),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
