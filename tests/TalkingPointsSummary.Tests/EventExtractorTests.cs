using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class EventExtractorTests : IDisposable
{
    private static readonly DateTime NewsSentAt = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset FixedNow = new(2027, 1, 10, 8, 0, 0, TimeSpan.Zero);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly AppDbContext _db;
    private readonly Mock<IAiClient> _mockAiClient = new();
    private readonly FixedTimeProvider _timeProvider = new(FixedNow);

    private AiCompletionRequest? _capturedRequest;

    public EventExtractorTests()
    {
        _db = CreateContext();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task ExtractAsync_ResolvesRelativeDatesAgainstNewsItemSentAtNotToday()
    {
        var newsItem = await SeedNewsItemAsync("Book fair is this Thursday.");

        RespondWith("""
            {
              "events": [
                { "title": "Book Fair", "event_date": "2026-03-05", "time_text": "9:00 AM", "replaces_event_id": null }
              ],
              "cancelled_event_ids": []
            }
            """);

        var created = await CreateExtractor().ExtractAsync(newsItem);

        // The prompt anchors on the news item's send date, and the reference calendar
        // spells out the absolute date "this Thursday" resolves to.
        _capturedRequest!.Prompt.Should().Contain("The news item below was sent on Monday, March 2, 2026.");
        _capturedRequest.Prompt.Should().Contain("Date sent: Monday, March 2, 2026");
        _capturedRequest.Prompt.Should().Contain("- 2026-03-05 = Thursday, March 5, 2026");
        _capturedRequest.Prompt.Should().Contain("Book fair is this Thursday.");

        // The wall clock is more than nine months later and never reaches the prompt.
        _capturedRequest.Prompt.Should().NotContain("2027");

        created.Should().HaveCount(1);

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.EventDate.Should().Be(new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc));
        stored.Title.Should().Be("Book Fair");
        stored.TimeText.Should().Be("9:00 AM");
        stored.School.Should().Be("Maple Elementary");
        stored.SourceNewsItemId.Should().Be(newsItem.Id);
        stored.Status.Should().Be(TrackedEventStatus.Active);
        stored.SupersededByEventId.Should().BeNull();

        // CreatedAt is the only field that comes from the clock rather than the news item.
        stored.CreatedAt.Should().Be(FixedNow.UtcDateTime);
    }

    [Fact]
    public async Task ExtractAsync_EveningSendThatIsAlreadyTheNextDayInUtc_AnchorsOnTheScheduleTimeZoneDate()
    {
        var newsItem = await SeedNewsItemAsync("Picture Day is tomorrow.");

        // 19:00 Monday in Los Angeles is 02:00 Tuesday in UTC, and SentAt is stored in UTC. A
        // school sending after 17:00 Pacific is ordinary, and anchoring the prompt on the UTC date
        // would resolve "tomorrow" to Wednesday, storing Picture Day a day after it happens.
        newsItem.SentAt = new DateTime(2026, 5, 12, 2, 0, 0, DateTimeKind.Utc);
        await _db.SaveChangesAsync();

        RespondWith("""{ "events": [], "cancelled_event_ids": [] }""");

        await CreateExtractor("America/Los_Angeles").ExtractAsync(newsItem);

        _capturedRequest!.Prompt.Should().Contain("The news item below was sent on Monday, May 11, 2026.");
        _capturedRequest.Prompt.Should().Contain("Date sent: Monday, May 11, 2026");
        _capturedRequest.Prompt.Should().Contain("- 2026-05-12 = Tuesday, May 12, 2026");
    }

    [Fact]
    public async Task ExtractAsync_EventAnnouncedAgain_DoesNotCreateASecondRow()
    {
        var newsItem = await SeedNewsItemAsync("Reminder: Spring Concert on March 20. Field Day is March 24.");
        await SeedTrackedEventAsync(newsItem, "Spring Concert", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));

        // The model re-announces the already-tracked concert and lists Field Day twice.
        RespondWith("""
            {
              "events": [
                { "title": "Spring Concert", "event_date": "2026-03-20", "time_text": "6:30 PM", "replaces_event_id": null },
                { "title": "Field Day", "event_date": "2026-03-24", "time_text": null, "replaces_event_id": null },
                { "title": "field day", "event_date": "2026-03-24", "time_text": null, "replaces_event_id": null }
              ],
              "cancelled_event_ids": []
            }
            """);

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().ContainSingle().Which.Title.Should().Be("Field Day");

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.OrderBy(e => e.EventDate).ToListAsync();
        stored.Should().HaveCount(2);
        stored[0].Title.Should().Be("Spring Concert");
        stored[0].TimeText.Should().BeNull("the pre-existing row is left untouched by a repeat announcement");
        stored[1].Title.Should().Be("Field Day");
    }

    [Fact]
    public async Task ExtractAsync_ExistingEventsAreOfferedToTheModelById()
    {
        var newsItem = await SeedNewsItemAsync("No new events.");
        var existing = await SeedTrackedEventAsync(
            newsItem, "Spring Concert", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc), "6:30 PM");

        // A superseded row must not be offered as a replace/cancel target.
        var retired = await SeedTrackedEventAsync(
            newsItem, "Old Assembly", new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc));
        retired.Status = TrackedEventStatus.Superseded;
        await _db.SaveChangesAsync();

        RespondWith("""{ "events": [], "cancelled_event_ids": [] }""");

        await CreateExtractor().ExtractAsync(newsItem);

        _capturedRequest!.Prompt.Should().Contain($"- id {existing.Id}: 2026-03-20 (6:30 PM) Spring Concert");
        _capturedRequest.Prompt.Should().NotContain("Old Assembly");
    }

    [Fact]
    public async Task ExtractAsync_ReplacesReference_MarksOldEventSupersededAndLinksTheNewRow()
    {
        var newsItem = await SeedNewsItemAsync("The Spring Concert has moved to March 27.");
        var original = await SeedTrackedEventAsync(
            newsItem, "Spring Concert", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc), "6:30 PM");

        RespondWith($$"""
            {
              "events": [
                { "title": "Spring Concert", "event_date": "2026-03-27", "time_text": "7:00 PM", "replaces_event_id": {{original.Id}} }
              ],
              "cancelled_event_ids": []
            }
            """);

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().ContainSingle();

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.OrderBy(e => e.EventDate).ToListAsync();
        stored.Should().HaveCount(2, "history stays auditable, the old row is never deleted");

        var superseded = stored.Single(e => e.EventDate == new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));
        var replacement = stored.Single(e => e.EventDate == new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc));

        superseded.Id.Should().Be(original.Id);
        superseded.Status.Should().Be(TrackedEventStatus.Superseded);
        superseded.SupersededByEventId.Should().Be(replacement.Id);

        replacement.Status.Should().Be(TrackedEventStatus.Active);
        replacement.SupersededByEventId.Should().BeNull();
        replacement.TimeText.Should().Be("7:00 PM");
    }

    [Fact]
    public async Task ExtractAsync_ReplacesReferenceToUnknownEvent_StillCreatesTheNewRow()
    {
        var newsItem = await SeedNewsItemAsync("Spring Concert on March 27.");

        RespondWith("""
            {
              "events": [
                { "title": "Spring Concert", "event_date": "2026-03-27", "time_text": null, "replaces_event_id": 9999 }
              ],
              "cancelled_event_ids": []
            }
            """);

        await CreateExtractor().ExtractAsync(newsItem);

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.Status.Should().Be(TrackedEventStatus.Active);
        stored.SupersededByEventId.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_CancelsReference_MarksEventCancelledAndKeepsTheRow()
    {
        var newsItem = await SeedNewsItemAsync("The Spring Concert is cancelled.");
        var original = await SeedTrackedEventAsync(
            newsItem, "Spring Concert", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc), "6:30 PM");

        RespondWith($$"""
            {
              "events": [],
              "cancelled_event_ids": [{{original.Id}}]
            }
            """);

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().BeEmpty();

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.Id.Should().Be(original.Id);
        stored.Status.Should().Be(TrackedEventStatus.Cancelled);
        stored.SupersededByEventId.Should().BeNull();
        stored.Title.Should().Be("Spring Concert");
    }

    [Fact]
    public async Task ExtractAsync_MalformedJson_ExtractsNothingWithoutThrowing()
    {
        var newsItem = await SeedNewsItemAsync("Book fair is this Thursday.");
        await SeedTrackedEventAsync(newsItem, "Spring Concert", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));

        RespondWith("I'm sorry, I can't help with that. {events: [ ");

        // A JsonException inside the extractor would surface here and fail the test.
        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().BeEmpty();

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.Title.Should().Be("Spring Concert");
        stored.Status.Should().Be(TrackedEventStatus.Active);
    }

    [Fact]
    public async Task ExtractAsync_EmptyModelText_ExtractsNothing()
    {
        // A thinking-first response can surface as empty text; it must not be read as "no events found"
        // in a way that throws or writes rows.
        var newsItem = await SeedNewsItemAsync("Book fair is this Thursday.");

        RespondWith(string.Empty);

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().BeEmpty();

        await using var verify = CreateContext();
        (await verify.TrackedEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExtractAsync_JsonWrappedInCodeFence_IsParsed()
    {
        var newsItem = await SeedNewsItemAsync("Picture day is March 10.");

        RespondWith("```json\n{ \"events\": [ { \"title\": \"Picture Day\", \"event_date\": \"2026-03-10\" } ], \"cancelled_event_ids\": [] }\n```");

        await CreateExtractor().ExtractAsync(newsItem);

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.Title.Should().Be("Picture Day");
        stored.EventDate.Should().Be(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc));
        stored.TimeText.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_UnparseableOrEmptyEntries_AreDiscardedButSiblingsSurvive()
    {
        var newsItem = await SeedNewsItemAsync("Several things coming up.");

        RespondWith("""
            {
              "events": [
                { "title": "Vague Thing", "event_date": "later this spring", "time_text": null },
                { "title": "   ", "event_date": "2026-03-12", "time_text": null },
                { "title": "Early Release", "event_date": "2026-03-12", "time_text": null }
              ],
              "cancelled_event_ids": []
            }
            """);

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().ContainSingle().Which.Title.Should().Be("Early Release");

        await using var verify = CreateContext();
        (await verify.TrackedEvents.SingleAsync()).Title.Should().Be("Early Release");
    }

    [Fact]
    public async Task ExtractAsync_UsesTheCategorizationProfileIncludingReasoningSettings()
    {
        var newsItem = await SeedNewsItemAsync("Book fair is this Thursday.");

        RespondWith("""{ "events": [], "cancelled_event_ids": [] }""");

        await CreateExtractor().ExtractAsync(newsItem);

        _capturedRequest!.ModelId.Should().Be("claude-haiku-4-5-20251001");
        _capturedRequest.MaxTokens.Should().Be(4096);
        _capturedRequest.Thinking.Should().Be(AiThinkingModes.Budget);
        _capturedRequest.ThinkingBudgetTokens.Should().Be(2048);
        _capturedRequest.Effort.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_NoChildMatchesButParentHasOneSchool_UsesThatSchool()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id, "Kid", "Maple Elementary");
        await SeedChildAsync(parent.Id, "Sibling", "Maple Elementary");
        var newsItem = await SeedNewsItemAsync("Book fair is this Thursday.", parent.Id, studentName: "Unknown Student");

        RespondWith("""
            {
              "events": [ { "title": "Book Fair", "event_date": "2026-03-05" } ],
              "cancelled_event_ids": []
            }
            """);

        await CreateExtractor().ExtractAsync(newsItem);

        await using var verify = CreateContext();
        (await verify.TrackedEvents.SingleAsync()).School.Should().Be("Maple Elementary");
    }

    [Fact]
    public async Task ExtractAsync_AmbiguousSchool_ExtractsNothing()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id, "Kid", "Maple Elementary");
        await SeedChildAsync(parent.Id, "Sibling", "Oak Middle");
        var newsItem = await SeedNewsItemAsync("Book fair is this Thursday.", parent.Id, studentName: "Unknown Student");

        RespondWith("""
            {
              "events": [ { "title": "Book Fair", "event_date": "2026-03-05" } ],
              "cancelled_event_ids": []
            }
            """);

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().BeEmpty();
        _capturedRequest.Should().BeNull("the model is never called when the school is unknown");

        await using var verify = CreateContext();
        (await verify.TrackedEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExtractAsync_SameTitleAndDateAtDifferentSchools_AreSeparateRows()
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id, "Kid", "Maple Elementary");
        await SeedChildAsync(parent.Id, "Sibling", "Oak Middle");

        var mapleNews = await SeedNewsItemAsync("Early release March 12.", parent.Id, studentName: "Kid");
        var oakNews = await SeedNewsItemAsync("Early release March 12.", parent.Id, studentName: "Sibling");

        RespondWith("""
            {
              "events": [ { "title": "Early Release", "event_date": "2026-03-12" } ],
              "cancelled_event_ids": []
            }
            """);

        var extractor = CreateExtractor();
        await extractor.ExtractAsync(mapleNews);
        await extractor.ExtractAsync(oakNews);

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.OrderBy(e => e.School).ToListAsync();
        stored.Should().HaveCount(2);
        stored[0].School.Should().Be("Maple Elementary");
        stored[1].School.Should().Be("Oak Middle");
        stored[0].Title.Should().Be("Early Release");
        stored[1].Title.Should().Be("Early Release");
    }

    [Theory]
    // The clock is fixed at January 10, 2027, so any of these that were guessed rather than
    // rejected would land on a plausible-looking but invented date.
    [InlineData("January 8")]
    [InlineData("Jan 8, 2027")]
    [InlineData("6:30 PM")]
    [InlineData("3:00 PM")]
    [InlineData("next Friday")]
    [InlineData("2026-13-01")]
    public async Task ExtractAsync_DateThatIsNotAnAbsoluteIsoDate_IsDiscardedRatherThanGuessed(string eventDate)
    {
        var newsItem = await SeedNewsItemAsync("The winter concert is coming up.");

        RespondWith(
            "{ \"events\": [ { \"title\": \"Winter Concert\", \"event_date\": \"" + eventDate + "\" } ], " +
            "\"cancelled_event_ids\": [] }");

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().BeEmpty();

        await using var verify = CreateContext();
        (await verify.TrackedEvents.CountAsync()).Should().Be(0);
    }

    [Theory]
    // The date component is kept exactly as written. Normalizing to UTC first would move a
    // 7:00 PM event in a US timezone onto the following day.
    [InlineData("2026-05-15T19:00:00-07:00")]
    [InlineData("2026-05-15T21:30:00-05:00")]
    [InlineData("2026-05-15T00:30:00+02:00")]
    [InlineData("2026-05-15 19:00")]
    [InlineData("2026-5-15")]
    [InlineData("2026-05-15")]
    public async Task ExtractAsync_TimestampWithAnOffset_StoresTheDateAsWritten(string eventDate)
    {
        var newsItem = await SeedNewsItemAsync("Spring concert on Friday.");

        RespondWith(
            "{ \"events\": [ { \"title\": \"Spring Concert\", \"event_date\": \"" + eventDate + "\" } ], " +
            "\"cancelled_event_ids\": [] }");

        await CreateExtractor().ExtractAsync(newsItem);

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.EventDate.Should().Be(new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ExtractAsync_OversizeTitleAndTimeText_AreTruncatedToTheMappedColumnWidths()
    {
        var newsItem = await SeedNewsItemAsync("A very wordy announcement.");

        // The prompt asks for 80 characters or fewer, but nothing enforces that. Postgres maps
        // Title to varchar(200) and TimeText to varchar(100) and rejects anything longer.
        var longTitle = new string('t', 300);
        var longTimeText = new string('m', 150);

        RespondWith(
            "{ \"events\": [ { \"title\": \"" + longTitle + "\", \"event_date\": \"2026-03-12\", " +
            "\"time_text\": \"" + longTimeText + "\" } ], \"cancelled_event_ids\": [] }");

        await CreateExtractor().ExtractAsync(newsItem);

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.Title.Should().Be(new string('t', 200));
        stored.TimeText.Should().Be(new string('m', 100));
    }

    [Fact]
    public async Task ExtractAsync_TitlesDifferingOnlyBeyondTheTruncationPoint_CollapseToOneRow()
    {
        var newsItem = await SeedNewsItemAsync("A very wordy announcement, twice.");

        var prefix = new string('t', 200);

        RespondWith(
            "{ \"events\": [ " +
            "{ \"title\": \"" + prefix + "aaa\", \"event_date\": \"2026-03-12\" }, " +
            "{ \"title\": \"" + prefix + "bbb\", \"event_date\": \"2026-03-12\" } ], " +
            "\"cancelled_event_ids\": [] }");

        var created = await CreateExtractor().ExtractAsync(newsItem);

        // Dedupe compares truncated titles, which is what the unique index sees.
        created.Should().ContainSingle();

        await using var verify = CreateContext();
        (await verify.TrackedEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ExtractAsync_ReAnnouncedEventThatWasSuperseded_DoesNotCreateASecondRow()
    {
        var newsItem = await SeedNewsItemAsync("Reminder: Spring Concert on March 20.");
        var retired = await SeedTrackedEventAsync(
            newsItem, "Spring Concert", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));
        retired.Status = TrackedEventStatus.Superseded;
        await _db.SaveChangesAsync();

        RespondWith("""
            {
              "events": [
                { "title": "Spring Concert", "event_date": "2026-03-20", "time_text": "6:30 PM" }
              ],
              "cancelled_event_ids": []
            }
            """);

        var created = await CreateExtractor().ExtractAsync(newsItem);

        // A second row would violate the unique index on (ParentId, School, EventDate, Title), and
        // a superseded row stays retired: the event moved, and the row that carries the new date is
        // the one that renders.
        created.Should().BeEmpty();

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.Id.Should().Be(retired.Id);
        stored.Status.Should().Be(TrackedEventStatus.Superseded);
    }

    [Fact]
    public async Task ExtractAsync_ReAnnouncedEventThatWasCancelled_ReinstatesTheExistingRow()
    {
        var newsItem = await SeedNewsItemAsync("Update: the Spring Concert IS happening on March 20 after all.");
        var cancelled = await SeedTrackedEventAsync(
            newsItem, "Spring Concert", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));
        cancelled.Status = TrackedEventStatus.Cancelled;
        await _db.SaveChangesAsync();

        // Cancelled rows are never shown to the model, so a reinstatement can only ever arrive as a
        // plain re-announcement. If that left the row cancelled, the event would be dropped from
        // Important Upcoming Dates permanently with no way back.
        RespondWith("""
            {
              "events": [
                { "title": "Spring Concert", "event_date": "2026-03-20", "time_text": "6:30 PM" }
              ],
              "cancelled_event_ids": []
            }
            """);

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().BeEmpty("nothing is inserted, the existing row comes back");

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.Id.Should().Be(cancelled.Id);
        stored.Status.Should().Be(TrackedEventStatus.Active);
        stored.SupersededByEventId.Should().BeNull();
        stored.SourceNewsItemId.Should().Be(newsItem.Id, "the news item that brought it back is the current source");
    }

    [Fact]
    public async Task ExtractAsync_CancelledEventBothReAnnouncedAndCancelledByOneResponse_StaysCancelled()
    {
        var newsItem = await SeedNewsItemAsync("The Spring Concert on March 20 is off.");
        var cancelled = await SeedTrackedEventAsync(
            newsItem, "Spring Concert", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));
        cancelled.Status = TrackedEventStatus.Cancelled;
        await _db.SaveChangesAsync();

        // A response that names the event in both lists contradicts itself. The explicit
        // cancellation wins, so a re-announcement never quietly undoes it.
        RespondWith($$"""
            {
              "events": [
                { "title": "Spring Concert", "event_date": "2026-03-20", "time_text": "6:30 PM" }
              ],
              "cancelled_event_ids": [{{cancelled.Id}}]
            }
            """);

        await CreateExtractor().ExtractAsync(newsItem);

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.Status.Should().Be(TrackedEventStatus.Cancelled);
    }

    [Fact]
    public async Task ExtractAsync_MovedEventLandingOnAnAlreadyTrackedDate_StillSupersedesTheOldRow()
    {
        var newsItem = await SeedNewsItemAsync("Field Day has moved to May 29, previously May 22.");

        var oldDate = await SeedTrackedEventAsync(
            newsItem, "Field Day", new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc));
        var newDate = await SeedTrackedEventAsync(
            newsItem, "Field Day", new DateTime(2026, 5, 29, 0, 0, 0, DateTimeKind.Utc));

        RespondWith($$"""
            {
              "events": [
                { "title": "Field Day", "event_date": "2026-05-29", "replaces_event_id": {{oldDate.Id}} }
              ],
              "cancelled_event_ids": []
            }
            """);

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().BeEmpty("the moved date is already tracked, so no row is added");

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.OrderBy(e => e.EventDate).ToListAsync();
        stored.Should().HaveCount(2);

        // Without this, Field Day would keep rendering on both May 22 and May 29 forever.
        stored[0].Id.Should().Be(oldDate.Id);
        stored[0].Status.Should().Be(TrackedEventStatus.Superseded);
        stored[0].SupersededByEventId.Should().Be(newDate.Id);

        stored[1].Id.Should().Be(newDate.Id);
        stored[1].Status.Should().Be(TrackedEventStatus.Active);
        stored[1].SupersededByEventId.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_EventThatReplacesItself_StaysActive()
    {
        var newsItem = await SeedNewsItemAsync("Field Day is still on May 22.");
        var existing = await SeedTrackedEventAsync(
            newsItem, "Field Day", new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc));

        RespondWith($$"""
            {
              "events": [
                { "title": "Field Day", "event_date": "2026-05-22", "replaces_event_id": {{existing.Id}} }
              ],
              "cancelled_event_ids": []
            }
            """);

        await CreateExtractor().ExtractAsync(newsItem);

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.Status.Should().Be(TrackedEventStatus.Active);
        stored.SupersededByEventId.Should().BeNull();
    }

    [Theory]
    [InlineData(TrackedEventStatus.Superseded)]
    [InlineData(TrackedEventStatus.Cancelled)]
    public async Task ExtractAsync_CorrectionMovingAnEventBackOntoARetiredRow_ReinstatesItAndRetiresTheOther(
        TrackedEventStatus retiredStatus)
    {
        var newsItem = await SeedNewsItemAsync("Correction: Field Day is back on May 22, not May 29.");

        var originalDate = await SeedTrackedEventAsync(
            newsItem, "Field Day", new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc));
        var movedDate = await SeedTrackedEventAsync(
            newsItem, "Field Day", new DateTime(2026, 5, 29, 0, 0, 0, DateTimeKind.Utc));

        // An earlier news item already retired the May 22 row.
        originalDate.Status = retiredStatus;
        if (retiredStatus == TrackedEventStatus.Superseded)
            originalDate.SupersededByEventId = movedDate.Id;
        await _db.SaveChangesAsync();

        RespondWith($$"""
            {
              "events": [
                { "title": "Field Day", "event_date": "2026-05-22", "replaces_event_id": {{movedDate.Id}} }
              ],
              "cancelled_event_ids": []
            }
            """);

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().BeEmpty("the corrected date is already a row, so nothing is inserted");

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.OrderBy(e => e.EventDate).ToListAsync();
        stored.Should().HaveCount(2);

        // Retiring the last active row in favour of a dead one would leave the two rows superseding
        // each other and drop Field Day from Important Upcoming Dates for good.
        stored[0].Id.Should().Be(originalDate.Id);
        stored[0].Status.Should().Be(TrackedEventStatus.Active);
        stored[0].SupersededByEventId.Should().BeNull();

        stored[1].Id.Should().Be(movedDate.Id);
        stored[1].Status.Should().Be(TrackedEventStatus.Superseded);
        stored[1].SupersededByEventId.Should().Be(originalDate.Id);
    }

    [Fact]
    public async Task ExtractAsync_CancelsReferenceToUnknownEvent_ChangesNothingWithoutThrowing()
    {
        var newsItem = await SeedNewsItemAsync("Something was cancelled.");
        var existing = await SeedTrackedEventAsync(
            newsItem, "Spring Concert", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));

        // A hallucinated identifier must not take the pipeline run down for this parent.
        RespondWith("""{ "events": [], "cancelled_event_ids": [9999] }""");

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().BeEmpty();

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.Id.Should().Be(existing.Id);
        stored.Status.Should().Be(TrackedEventStatus.Active);
    }

    [Theory]
    [InlineData(TrackedEventStatus.Cancelled)]
    [InlineData(TrackedEventStatus.Superseded)]
    public async Task ExtractAsync_CancelsReferenceToAnAlreadyRetiredEvent_LeavesItsStatusAlone(
        TrackedEventStatus retiredStatus)
    {
        var newsItem = await SeedNewsItemAsync("The concert is cancelled.");
        var retired = await SeedTrackedEventAsync(
            newsItem, "Spring Concert", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));
        retired.Status = retiredStatus;
        await _db.SaveChangesAsync();

        RespondWith($$"""{ "events": [], "cancelled_event_ids": [{{retired.Id}}] }""");

        await CreateExtractor().ExtractAsync(newsItem);

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.SingleAsync();
        stored.Status.Should().Be(retiredStatus, "a retired row is never re-retired under a new status");
    }

    [Fact]
    public async Task ExtractAsync_CancelsAnEventSupersededByTheSameResponse_LeavesItSuperseded()
    {
        var newsItem = await SeedNewsItemAsync("The Spring Concert moves to March 27; the old date is off.");
        var original = await SeedTrackedEventAsync(
            newsItem, "Spring Concert", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));

        // The model both replaces and cancels the same row. The supersession is applied first and
        // the cancellation must not overwrite it, or the link to the new date is lost.
        RespondWith($$"""
            {
              "events": [
                { "title": "Spring Concert", "event_date": "2026-03-27", "replaces_event_id": {{original.Id}} }
              ],
              "cancelled_event_ids": [{{original.Id}}]
            }
            """);

        await CreateExtractor().ExtractAsync(newsItem);

        await using var verify = CreateContext();
        var stored = await verify.TrackedEvents.OrderBy(e => e.EventDate).ToListAsync();
        stored.Should().HaveCount(2);

        stored[0].Id.Should().Be(original.Id);
        stored[0].Status.Should().Be(TrackedEventStatus.Superseded);
        stored[0].SupersededByEventId.Should().Be(stored[1].Id);
        stored[1].Status.Should().Be(TrackedEventStatus.Active);
    }

    [Fact]
    public async Task ExtractAsync_TruncatedResponse_ExtractsNothingWithoutThrowing()
    {
        var newsItem = await SeedNewsItemAsync("A newsletter listing many dated events.");

        // The categorization profile can hit its token ceiling on a long newsletter. The partial
        // text reaches the extractor, which discards it rather than failing the run.
        RespondWith(
            """{ "events": [ { "title": "Field Day", "event_date": "2026-05-2""",
            "max_tokens");

        var created = await CreateExtractor().ExtractAsync(newsItem);

        created.Should().BeEmpty();

        await using var verify = CreateContext();
        (await verify.TrackedEvents.CountAsync()).Should().Be(0);
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;
        return new AppDbContext(options);
    }

    private EventExtractor CreateExtractor(string timeZone = "UTC")
        => new(
            _mockAiClient.Object,
            _db,
            Options.Create(new AiOptions
            {
                Provider = "Anthropic",
                Profiles = new AiProfilesOptions
                {
                    Categorization = new AiProfileOptions
                    {
                        ModelId = "claude-haiku-4-5-20251001",
                        MaxTokens = 4096,
                        Thinking = AiThinkingModes.Budget,
                        ThinkingBudgetTokens = 2048
                    }
                }
            }),
            Options.Create(new PipelineScheduleOptions { TimeZone = timeZone }),
            NullLogger<EventExtractor>.Instance,
            _timeProvider);

    private void RespondWith(string responseText, string? stopReason = null)
    {
        _mockAiClient
            .Setup(client => client.CompleteAsync(It.IsAny<AiCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AiCompletionRequest, CancellationToken>((request, _) => _capturedRequest = request)
            .ReturnsAsync(new AiCompletionResult(responseText, null, stopReason));
    }

    private async Task<Parent> SeedParentAsync()
    {
        var parent = new Parent
        {
            Name = "Test Parent",
            TalkingPointsToken = "token",
            TalkingPointsContactId = "contact",
            EmailRecipients = "parent@example.com",
            IsActive = true
        };
        _db.Parents.Add(parent);
        await _db.SaveChangesAsync();
        return parent;
    }

    private async Task<Child> SeedChildAsync(int parentId, string name, string school)
    {
        var child = new Child
        {
            ParentId = parentId,
            Name = name,
            School = school,
            StartingGrade = 3,
            StartingYear = 2024,
            Emoji = "book"
        };
        _db.Children.Add(child);
        await _db.SaveChangesAsync();
        return child;
    }

    private async Task<NewsItem> SeedNewsItemAsync(string newsContent)
    {
        var parent = await SeedParentAsync();
        await SeedChildAsync(parent.Id, "Kid", "Maple Elementary");
        return await SeedNewsItemAsync(newsContent, parent.Id, "Kid");
    }

    private async Task<NewsItem> SeedNewsItemAsync(string newsContent, int parentId, string studentName)
    {
        var newsItem = new NewsItem
        {
            ParentId = parentId,
            SourceMessageId = Guid.NewGuid().ToString(),
            SourceType = SourceType.NewsletterUrl,
            NewsContent = newsContent,
            AiSummary = "summary",
            FromName = "Ms. Zutz",
            StudentName = studentName,
            SentAt = NewsSentAt,
            AnalyzedAt = NewsSentAt,
            CreatedAt = NewsSentAt
        };
        _db.NewsItems.Add(newsItem);
        await _db.SaveChangesAsync();
        return newsItem;
    }

    private async Task<TrackedEvent> SeedTrackedEventAsync(
        NewsItem newsItem,
        string title,
        DateTime eventDate,
        string? timeText = null)
    {
        var trackedEvent = new TrackedEvent
        {
            ParentId = newsItem.ParentId,
            SourceNewsItemId = newsItem.Id,
            School = "Maple Elementary",
            EventDate = eventDate,
            Title = title,
            TimeText = timeText,
            Status = TrackedEventStatus.Active,
            CreatedAt = NewsSentAt
        };
        _db.TrackedEvents.Add(trackedEvent);
        await _db.SaveChangesAsync();
        return trackedEvent;
    }
}
