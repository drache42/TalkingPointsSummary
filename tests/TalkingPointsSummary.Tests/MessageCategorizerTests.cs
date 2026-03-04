using System.Text.Json;
using FluentAssertions;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class MessageCategorizerTests
{
    [Fact]
    public void CategorizationJsonResponse_ParsesValidJson()
    {
        var json = """
            {
              "message_id": "msg-001",
              "has_newsletter_url": true,
              "newsletter_url": "https://www.smore.com/abc123",
              "is_news_itself": false,
              "summary": "Weekly newsletter link"
            }
            """;

        var result = JsonSerializer.Deserialize<CategorizationJsonResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        result.Should().NotBeNull();
        result!.MessageId.Should().Be("msg-001");
        result.HasNewsletterUrl.Should().BeTrue();
        result.NewsletterUrl.Should().Be("https://www.smore.com/abc123");
        result.IsNewsItself.Should().BeFalse();
        result.Summary.Should().Be("Weekly newsletter link");
    }

    [Fact]
    public void CategorizationJsonResponse_ParsesDirectNewsJson()
    {
        var json = """
            {
              "message_id": "msg-002",
              "has_newsletter_url": false,
              "newsletter_url": null,
              "is_news_itself": true,
              "summary": "School picture day is next Friday"
            }
            """;

        var result = JsonSerializer.Deserialize<CategorizationJsonResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        result.Should().NotBeNull();
        result!.HasNewsletterUrl.Should().BeFalse();
        result.NewsletterUrl.Should().BeNull();
        result.IsNewsItself.Should().BeTrue();
    }

    [Fact]
    public void CategorizationJsonResponse_HandlesBothNewsletterAndNews()
    {
        var json = """
            {
              "message_id": "msg-003",
              "has_newsletter_url": true,
              "newsletter_url": "https://www.smore.com/xyz",
              "is_news_itself": true,
              "summary": "Newsletter link with additional event info"
            }
            """;

        var result = JsonSerializer.Deserialize<CategorizationJsonResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        result.Should().NotBeNull();
        result!.HasNewsletterUrl.Should().BeTrue();
        result.IsNewsItself.Should().BeTrue();
    }

    [Fact]
    public void CategorizationResult_DefaultsCorrectly()
    {
        var result = new CategorizationResult();

        result.MessageId.Should().BeEmpty();
        result.HasNewsletterUrl.Should().BeFalse();
        result.NewsletterUrl.Should().BeNull();
        result.IsNewsItself.Should().BeFalse();
        result.Summary.Should().BeEmpty();
    }

    [Fact]
    public void StripCodeFences_HandlesWrappedJson()
    {
        // Simulate what the code does to strip markdown fences
        var wrapped = "```json\n{\"message_id\": \"msg-001\"}\n```";
        var stripped = System.Text.RegularExpressions.Regex.Replace(wrapped, @"```json|```", "").Trim();

        var result = JsonSerializer.Deserialize<CategorizationJsonResponse>(stripped,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        result.Should().NotBeNull();
        result!.MessageId.Should().Be("msg-001");
    }

    [Fact]
    public void StripCodeFences_HandlesCleanJson()
    {
        var clean = "{\"message_id\": \"msg-001\", \"has_newsletter_url\": false, \"is_news_itself\": true, \"summary\": \"test\"}";
        var stripped = System.Text.RegularExpressions.Regex.Replace(clean, @"```json|```", "").Trim();

        var result = JsonSerializer.Deserialize<CategorizationJsonResponse>(stripped,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        result.Should().NotBeNull();
        result!.MessageId.Should().Be("msg-001");
    }
}
