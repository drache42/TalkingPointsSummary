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

        var prompt = builder.Build(message, TimeZoneInfo.Utc);

        prompt.Should().Be("From=Ms. Zutz|Date=Saturday, March 7, 2026 12:19 AM (school local time, UTC+00:00)|Text=Here is the newsletter: https://app.smore.com/n/rekj9|Id=abc123");
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

        var prompt = builder.Build(message, TimeZoneInfo.Utc);

        // Characters should pass through unchanged — this goes to an LLM, not HTML
        prompt.Should().Contain("<b>Bold</b>");
        prompt.Should().Contain("& \"quotes\"");
        prompt.Should().Contain("<script>");
    }

    [Fact]
    public void Build_DateSent_IsRenderedInSuppliedLocalTimeZone()
    {
        var builder = new MessageCategorizationPromptBuilder("{{DATE_SENT}}");
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var message = new Message
        {
            FromName = "Teacher",
            // 2026-05-15 01:30 UTC == 2026-05-14 21:30 America/New_York (EDT, UTC-04:00)
            SentAt = new DateTime(2026, 5, 15, 1, 30, 45, DateTimeKind.Utc),
            MessageText = "Test",
            ExternalMessageId = "msg-date"
        };

        var prompt = builder.Build(message, eastern);

        prompt.Should().Be("Thursday, May 14, 2026 9:30 PM (school local time, UTC-04:00)");
    }
}