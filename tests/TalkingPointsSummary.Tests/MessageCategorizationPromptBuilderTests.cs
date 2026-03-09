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
}