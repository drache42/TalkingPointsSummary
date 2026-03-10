using FluentAssertions;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class MessageCategorizationPromptBuilderTests
{
    [Fact]
    public void Build_ReplacesAllTokens()
    {
        var builder = new MessageCategorizationPromptBuilder(
            "From={{FROM_NAME}}|Date={{DATE_SENT}}|Text={{MESSAGE_TEXT}}|Id={{MESSAGE_ID}}");

        var message = new Message
        {
            FromName = "Ms. Zutz",
            SentAt = new DateTime(2026, 3, 7, 0, 19, 28, DateTimeKind.Utc),
            MessageText = "Here is the newsletter: https://app.smore.com/n/rekj9",
            ExternalMessageId = "abc123"
        };

        var prompt = builder.Build(message);

        prompt.Should().Be("From=Ms. Zutz|Date=2026-03-07T00:19:28.0000000Z|Text=Here is the newsletter: https://app.smore.com/n/rekj9|Id=abc123");
    }

    [Fact]
    public void Build_SpecialCharactersAreNotEscaped()
    {
        var builder = new MessageCategorizationPromptBuilder(
            "Text={{MESSAGE_TEXT}}");

        var message = new Message
        {
            FromName = "Teacher",
            SentAt = DateTime.UtcNow,
            MessageText = "<b>Bold</b> & \"quotes\" <script>alert('xss')</script>",
            ExternalMessageId = "msg-special"
        };

        var prompt = builder.Build(message);

        // Characters should pass through unchanged — this goes to an LLM, not HTML
        prompt.Should().Contain("<b>Bold</b>");
        prompt.Should().Contain("& \"quotes\"");
        prompt.Should().Contain("<script>");
    }

    [Fact]
    public void Build_DateSent_IsIso8601RoundTripFormat()
    {
        var builder = new MessageCategorizationPromptBuilder("{{DATE_SENT}}");

        var message = new Message
        {
            FromName = "Teacher",
            SentAt = new DateTime(2026, 3, 7, 14, 30, 45, DateTimeKind.Utc),
            MessageText = "Test",
            ExternalMessageId = "msg-date"
        };

        var prompt = builder.Build(message);

        // ISO 8601 round-trip format ("O") should produce something like 2026-03-07T14:30:45.0000000Z
        prompt.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$");
    }
}