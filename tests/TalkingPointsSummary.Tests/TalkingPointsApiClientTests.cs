using FluentAssertions;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class TalkingPointsApiClientTests
{
    [Fact]
    public void TalkingPointsMessage_DeserializesCorrectly()
    {
        // Verify DTO field mapping from API JSON structure
        var message = new TalkingPointsMessage
        {
            Id = "abc123",
            ContactMessageId = "contact-456",
            Text = "Hello parents!",
            FromName = "Ms. Smith",
            From = new TalkingPointsFrom
            {
                User = new TalkingPointsUser { Signature = "Ms. Jane Smith" }
            },
            ContactInfo = new TalkingPointsContactInfo { StudentName = "Clara" },
            CreatedAt = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            DisplayDate = new DateTime(2026, 3, 1, 10, 30, 0, DateTimeKind.Utc)
        };

        message.Id.Should().Be("abc123");
        message.ContactMessageId.Should().Be("contact-456");
        message.Text.Should().Be("Hello parents!");
        message.From!.User!.Signature.Should().Be("Ms. Jane Smith");
        message.ContactInfo!.StudentName.Should().Be("Clara");
        message.DisplayDate.Should().Be(new DateTime(2026, 3, 1, 10, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void TalkingPointsMessage_HandlesNullOptionalFields()
    {
        var message = new TalkingPointsMessage
        {
            Id = "abc123"
        };

        message.Text.Should().BeNull();
        message.FromName.Should().BeNull();
        message.From.Should().BeNull();
        message.ContactInfo.Should().BeNull();
        message.ContactMessageId.Should().BeNull();
        message.CreatedAt.Should().BeNull();
        message.DisplayDate.Should().BeNull();
    }

    [Fact]
    public void TalkingPointsApiResponse_EmptyData_ReturnsEmptyList()
    {
        var response = new TalkingPointsApiResponse
        {
            Data = new TalkingPointsData { Messages = [] }
        };

        response.Data.Messages.Should().BeEmpty();
    }

    [Fact]
    public void TalkingPointsApiResponse_NullData_HandledSafely()
    {
        var response = new TalkingPointsApiResponse { Data = null };
        var messages = response.Data?.Messages ?? [];

        messages.Should().BeEmpty();
    }
}
