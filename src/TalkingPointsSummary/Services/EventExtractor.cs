using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Uses the configured AI provider to extract dated school events from a news item and
/// persist them as tracked events. Relative date references in the news text are resolved
/// against the news item's own send date, read in the configured schedule timezone, and repeated
/// announcements of the same event collapse onto a single row.
/// </summary>
public partial class EventExtractor : IEventExtractor
{
    /// <summary>
    /// Matches the mapped column width of <see cref="TrackedEvent.Title"/>. A longer title from the
    /// model is truncated here rather than failing the insert against the database.
    /// </summary>
    private const int MaxTitleLength = 200;

    /// <summary>
    /// Matches the mapped column width of <see cref="TrackedEvent.TimeText"/>.
    /// </summary>
    private const int MaxTimeTextLength = 100;

    /// <summary>
    /// The only date forms accepted from the model. Both are absolute, so no part of the date is
    /// ever taken from the machine clock.
    /// </summary>
    private static readonly string[] EventDateFormats = ["yyyy-MM-dd", "yyyy-M-d"];

    /// <summary>
    /// Characters that begin the time portion of an ISO timestamp.
    /// </summary>
    private static readonly char[] DateTimeSeparators = ['T', 't', ' '];

    private static readonly EventExtractionPromptBuilder PromptBuilder = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAiClient _aiClient;
    private readonly AppDbContext _db;
    private readonly AiOptions _options;
    private readonly ILogger<EventExtractor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _scheduleTimeZone;

    /// <summary>
    /// Initializes an event extractor.
    /// </summary>
    /// <param name="aiClient">AI client used to send extraction requests.</param>
    /// <param name="db">Database context used to read and persist tracked events.</param>
    /// <param name="aiOptions">AI configuration including the categorization profile.</param>
    /// <param name="schedule">
    /// Pipeline schedule configuration, used for the local timezone the news item's send date is
    /// read in so relative date references resolve against the school's calendar day.
    /// </param>
    /// <param name="logger">Logger used for extraction diagnostics.</param>
    /// <param name="timeProvider">Optional time provider used to stamp created rows.</param>
    public EventExtractor(
        IAiClient aiClient,
        AppDbContext db,
        IOptions<AiOptions> aiOptions,
        IOptions<PipelineScheduleOptions> schedule,
        ILogger<EventExtractor> logger,
        TimeProvider? timeProvider = null)
    {
        _aiClient = aiClient;
        _db = db;
        _options = aiOptions.Value;
        _scheduleTimeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.Value.TimeZone);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TrackedEvent>> ExtractAsync(NewsItem newsItem, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newsItem);

        var school = await ResolveSchoolAsync(newsItem, ct);
        if (school is null)
        {
            _logger.LogWarning(
                "Could not resolve a school for news item {NewsItemId} (student '{StudentName}'), skipping event extraction",
                newsItem.Id, newsItem.StudentName);
            return Array.Empty<TrackedEvent>();
        }

        var parentEvents = await _db.TrackedEvents
            .Where(trackedEvent => trackedEvent.ParentId == newsItem.ParentId)
            .ToListAsync(ct);

        var schoolEvents = parentEvents
            .Where(trackedEvent => string.Equals(trackedEvent.School, school, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var activeEvents = schoolEvents
            .Where(trackedEvent => trackedEvent.Status == TrackedEventStatus.Active)
            .OrderBy(trackedEvent => trackedEvent.EventDate)
            .ToList();

        var cancelledEvents = schoolEvents
            .Where(trackedEvent => trackedEvent.Status == TrackedEventStatus.Cancelled)
            .OrderBy(trackedEvent => trackedEvent.EventDate)
            .ToList();

        var response = await RequestExtractionAsync(newsItem, school, activeEvents, cancelledEvents, ct);
        if (response is null)
            return Array.Empty<TrackedEvent>();

        // Reinstatements are applied first so a row the news item brings back is already active
        // when the duplicate scan below meets it again in the events list.
        var reinstatedByRequest = ApplyReinstatements(response, newsItem, schoolEvents, activeEvents);

        var batch = CreateNewEvents(newsItem, school, response, schoolEvents, activeEvents);

        if (batch.Created.Count > 0 || batch.Reinstated || reinstatedByRequest)
            await _db.SaveChangesAsync(ct);

        var statusChanged = ApplyReplacements(batch.Replacements);
        statusChanged |= ApplyCancellations(response, activeEvents);

        if (statusChanged)
            await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Extracted {CreatedCount} new event(s) for news item {NewsItemId} at {School}",
            batch.Created.Count, newsItem.Id, school);

        return batch.Created;
    }

    private async Task<EventExtractionJsonResponse?> RequestExtractionAsync(
        NewsItem newsItem,
        string school,
        IReadOnlyList<TrackedEvent> activeEvents,
        IReadOnlyList<TrackedEvent> cancelledEvents,
        CancellationToken ct)
    {
        var prompt = PromptBuilder.Build(newsItem, school, activeEvents, cancelledEvents, _scheduleTimeZone);
        var profile = _options.Profiles.Categorization;

        var aiResult = await _aiClient.CompleteAsync(
            new AiCompletionRequest(
                prompt,
                profile.ModelId,
                profile.MaxTokens,
                profile.Thinking,
                profile.ThinkingBudgetTokens,
                profile.Effort),
            ct);

        if (string.IsNullOrWhiteSpace(aiResult.Text))
        {
            _logger.LogWarning(
                "AI event extraction returned no text for news item {NewsItemId}, extracting nothing",
                newsItem.Id);
            return null;
        }

        var text = StripCodeFences().Replace(aiResult.Text, "").Trim();

        try
        {
            return JsonSerializer.Deserialize<EventExtractionJsonResponse>(text, SerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse AI event extraction response for news item {NewsItemId}. Raw: {Text}",
                newsItem.Id, text);
            return null;
        }
    }

    private ExtractionBatch CreateNewEvents(
        NewsItem newsItem,
        string school,
        EventExtractionJsonResponse response,
        List<TrackedEvent> schoolEvents,
        List<TrackedEvent> activeEvents)
    {
        var created = new List<TrackedEvent>();
        var replacements = new List<EventReplacement>();
        var reinstated = false;

        var candidates = response.Events ?? new List<EventExtractionJsonEvent>();
        var cancelledIds = response.CancelledEventIds is { Count: > 0 } ids
            ? new HashSet<int>(ids)
            : null;
        var now = _timeProvider.GetUtcDateTime();

        foreach (var candidate in candidates)
        {
            var title = Truncate(candidate.Title?.Trim(), MaxTitleLength);
            if (string.IsNullOrEmpty(title))
                continue;

            if (!TryParseEventDate(candidate.EventDate, out var eventDate))
            {
                _logger.LogDebug(
                    "Discarding event '{Title}' from news item {NewsItemId}: unparseable date '{EventDate}'",
                    title, newsItem.Id, candidate.EventDate);
                continue;
            }

            // Deliberately scans every status, not just the active rows: re-announcing an event
            // that was already cancelled or superseded must not insert a second row, which the
            // unique index on (ParentId, School, EventDate, Title) would reject anyway.
            var duplicate = schoolEvents.FirstOrDefault(existing => IsSameEvent(existing, eventDate, title))
                ?? created.FirstOrDefault(pending => IsSameEvent(pending, eventDate, title));

            if (duplicate is not null)
            {
                _logger.LogDebug(
                    "Skipping duplicate event '{Title}' on {EventDate:yyyy-MM-dd} at {School}",
                    title, eventDate, school);

                // A supersession is inferred from a date move, never stated outright, so a later
                // news item putting the event back on its original date is exactly the evidence
                // needed to undo it. The model can only ever express that as a plain
                // re-announcement, because it is never shown inactive rows.
                if (duplicate.Status == TrackedEventStatus.Superseded
                    && cancelledIds?.Contains(duplicate.Id) != true)
                {
                    ReinstateEvent(duplicate, newsItem, schoolEvents);
                    reinstated = true;
                }
                else if (duplicate.Status == TrackedEventStatus.Cancelled)
                {
                    // A cancellation is a positive statement that the event is off, and a
                    // re-announcement is not evidence that it was called back on. School
                    // newsletters reprint a standing monthly calendar, which re-announces every
                    // event on it, cancelled ones included, and nothing in that text tells the
                    // model the difference. Reviving here would put a cancelled event back into
                    // Important Upcoming Dates every week until its date passed, so a genuine
                    // reinstatement has to arrive through reinstated_event_ids, where the model is
                    // shown the cancelled row and has to name it deliberately.
                    _logger.LogInformation(
                        "Event {EventId} is re-announced by news item {NewsItemId} but stays cancelled; "
                        + "a cancelled event is only revived when the model names it in reinstated_event_ids",
                        duplicate.Id, newsItem.Id);
                }

                // The row is a duplicate but the supersession it carries is not: a moved event can
                // land on a date and title that is already tracked, and dropping the reference here
                // would leave the old date Active and rendering forever.
                TryQueueReplacement(newsItem, candidate, duplicate, activeEvents, replacements);
                continue;
            }

            var trackedEvent = new TrackedEvent
            {
                ParentId = newsItem.ParentId,
                SourceNewsItemId = newsItem.Id,
                School = school,
                EventDate = eventDate,
                Title = title,
                TimeText = Truncate(candidate.TimeText?.Trim(), MaxTimeTextLength),
                Status = TrackedEventStatus.Active,
                CreatedAt = now
            };

            _db.TrackedEvents.Add(trackedEvent);
            created.Add(trackedEvent);

            TryQueueReplacement(newsItem, candidate, trackedEvent, activeEvents, replacements);
        }

        return new ExtractionBatch(created, replacements, reinstated);
    }

    /// <summary>
    /// Brings an inactive row back and retires whatever row is currently standing in for it.
    /// </summary>
    /// <remarks>
    /// An event can be moved more than once: March 20 is superseded by March 27, which is itself
    /// superseded by April 3. Only the last row in that chain is still active, so demoting just the
    /// row the revived one points at would leave the chain's live tail active alongside it and
    /// render the same event on two different dates, which is the conflicting-event defect the
    /// critic hunts for. The chain is walked to its live end instead, and the visited set stops a
    /// corrupted chain that loops back on itself from spinning.
    /// </remarks>
    /// <param name="revived">Row being brought back to active.</param>
    /// <param name="newsItem">News item that brought it back.</param>
    /// <param name="schoolEvents">Every tracked row for this parent and school, any status.</param>
    private void ReinstateEvent(TrackedEvent revived, NewsItem newsItem, List<TrackedEvent> schoolEvents)
    {
        var visited = new HashSet<int> { revived.Id };
        var nextId = revived.SupersededByEventId;

        while (nextId is int successorId && visited.Add(successorId))
        {
            var successor = schoolEvents.FirstOrDefault(existing => existing.Id == successorId);
            if (successor is null)
                break;

            if (successor.Status == TrackedEventStatus.Active)
            {
                _logger.LogInformation(
                    "Event {SuccessorId} superseded by {EventId}, which news item {NewsItemId} moved back",
                    successor.Id, revived.Id, newsItem.Id);

                successor.Status = TrackedEventStatus.Superseded;
                successor.SupersededByEventId = revived.Id;
                break;
            }

            nextId = successor.SupersededByEventId;
        }

        _logger.LogInformation(
            "Event {EventId} reinstated from {PreviousStatus} because news item {NewsItemId} announces it again",
            revived.Id, revived.Status, newsItem.Id);

        revived.Status = TrackedEventStatus.Active;
        revived.SupersededByEventId = null;
        revived.SourceNewsItemId = newsItem.Id;
    }

    /// <summary>
    /// Applies the reinstatements the model named outright, the only way a cancelled event comes
    /// back.
    /// </summary>
    /// <remarks>
    /// The cancelled rows are listed in the prompt for exactly this purpose, so the model can say
    /// "this news item states the cancelled Field Day is back on" instead of the pipeline having to
    /// infer it from a re-announcement that a reprinted calendar produces every month.
    /// </remarks>
    /// <returns><c>true</c> when at least one row changed status.</returns>
    private bool ApplyReinstatements(
        EventExtractionJsonResponse response,
        NewsItem newsItem,
        List<TrackedEvent> schoolEvents,
        List<TrackedEvent> activeEvents)
    {
        var reinstatedIds = response.ReinstatedEventIds;
        if (reinstatedIds is not { Count: > 0 })
            return false;

        var cancelledIds = response.CancelledEventIds is { Count: > 0 } ids
            ? new HashSet<int>(ids)
            : null;

        var changed = false;

        foreach (var reinstatedId in reinstatedIds)
        {
            var target = schoolEvents.FirstOrDefault(existing => existing.Id == reinstatedId);

            if (target is null || target.Status == TrackedEventStatus.Active)
            {
                _logger.LogDebug(
                    "Ignoring reinstates reference to unknown or already active event {EventId}", reinstatedId);
                continue;
            }

            // A response that reinstates and cancels the same event contradicts itself. The
            // cancellation wins, exactly as it does for a re-announcement.
            if (cancelledIds?.Contains(reinstatedId) == true)
            {
                _logger.LogInformation(
                    "Event {EventId} is both reinstated and cancelled by news item {NewsItemId}; "
                    + "the cancellation wins",
                    reinstatedId, newsItem.Id);
                continue;
            }

            ReinstateEvent(target, newsItem, schoolEvents);

            // Now that it is active again it is a legitimate target for a supersession or a
            // cancellation declared later in the same response.
            if (!activeEvents.Contains(target))
                activeEvents.Add(target);

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Queues the supersession a candidate declares, pointing the replaced event at whichever row
    /// survives for that date and title, whether it was just created or was already tracked.
    /// </summary>
    private void TryQueueReplacement(
        NewsItem newsItem,
        EventExtractionJsonEvent candidate,
        TrackedEvent replacement,
        List<TrackedEvent> activeEvents,
        List<EventReplacement> replacements)
    {
        if (candidate.ReplacesEventId is not int replacedId)
            return;

        var replaced = activeEvents.FirstOrDefault(existing => existing.Id == replacedId);
        if (replaced is null)
        {
            _logger.LogDebug(
                "Ignoring replaces reference to unknown or inactive event {EventId} from news item {NewsItemId}",
                replacedId, newsItem.Id);
            return;
        }

        if (ReferenceEquals(replaced, replacement))
        {
            _logger.LogDebug(
                "Ignoring event {EventId} replacing itself from news item {NewsItemId}",
                replacedId, newsItem.Id);
            return;
        }

        replacements.Add(new EventReplacement(replaced, replacement));
    }

    private bool ApplyReplacements(List<EventReplacement> replacements)
    {
        var changed = false;

        foreach (var replacement in replacements)
        {
            if (replacement.Replaced.Status != TrackedEventStatus.Active)
                continue;

            var survivor = replacement.Replacement;

            // The surviving row is not always active. A correction that moves an event back onto a
            // date it previously held resolves onto the row that an earlier news item superseded or
            // cancelled. Retiring the last active row in favour of a dead one would leave the two
            // rows superseding each other and drop the event from tracking for good, so the row the
            // news item names as the survivor is reinstated first.
            if (survivor.Status != TrackedEventStatus.Active)
            {
                _logger.LogInformation(
                    "Event {EventId} reinstated from {OldStatus} because it is named as the replacement for event {ReplacedEventId}",
                    survivor.Id, survivor.Status, replacement.Replaced.Id);

                survivor.Status = TrackedEventStatus.Active;
                survivor.SupersededByEventId = null;
            }

            replacement.Replaced.Status = TrackedEventStatus.Superseded;
            replacement.Replaced.SupersededByEventId = survivor.Id;
            changed = true;

            _logger.LogInformation(
                "Event {OldEventId} superseded by {NewEventId}",
                replacement.Replaced.Id, survivor.Id);
        }

        return changed;
    }

    private bool ApplyCancellations(EventExtractionJsonResponse response, List<TrackedEvent> activeEvents)
    {
        var changed = false;
        var cancelledIds = response.CancelledEventIds ?? new List<int>();

        foreach (var cancelledId in cancelledIds)
        {
            var target = activeEvents.FirstOrDefault(existing => existing.Id == cancelledId);
            if (target is null || target.Status != TrackedEventStatus.Active)
            {
                _logger.LogDebug(
                    "Ignoring cancels reference to unknown or inactive event {EventId}", cancelledId);
                continue;
            }

            target.Status = TrackedEventStatus.Cancelled;
            changed = true;

            _logger.LogInformation("Event {EventId} marked cancelled", target.Id);
        }

        return changed;
    }

    private async Task<string?> ResolveSchoolAsync(NewsItem newsItem, CancellationToken ct)
    {
        var children = await _db.Children
            .Where(child => child.ParentId == newsItem.ParentId)
            .ToListAsync(ct);

        if (children.Count == 0)
            return null;

        var match = children.FirstOrDefault(child =>
            string.Equals(child.Name, newsItem.StudentName, StringComparison.OrdinalIgnoreCase));

        if (match is not null && !string.IsNullOrWhiteSpace(match.School))
            return match.School;

        var schools = children
            .Select(child => child.School)
            .Where(school => !string.IsNullOrWhiteSpace(school))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return schools.Count == 1 ? schools[0] : null;
    }

    /// <summary>
    /// Parses a model-supplied event date into a UTC date with no time component.
    /// </summary>
    /// <remarks>
    /// Only the absolute YYYY-MM-DD form the prompt asks for is accepted. A permissive parse would
    /// take the year from the machine clock for "January 8" and turn a bare "6:30 PM" into today,
    /// inventing events rather than discarding an unusable answer. When the model returns a full
    /// timestamp, the date is taken exactly as written: converting an offset-bearing timestamp to
    /// UTC first would push an evening event onto the following day.
    /// </remarks>
    /// <param name="value">Date text returned by the model.</param>
    /// <param name="eventDate">Parsed UTC date when parsing succeeds.</param>
    /// <returns><c>true</c> when the value parsed into an absolute date.</returns>
    internal static bool TryParseEventDate(string? value, out DateTime eventDate)
    {
        eventDate = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        var timeStart = text.IndexOfAny(DateTimeSeparators);
        if (timeStart > 0)
            text = text[..timeStart];

        if (!DateTime.TryParseExact(
                text,
                EventDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        eventDate = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
        return true;
    }

    private static bool IsSameEvent(TrackedEvent trackedEvent, DateTime eventDate, string title)
        => trackedEvent.EventDate.Date == eventDate.Date
            && string.Equals(trackedEvent.Title, title, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cuts a value to the mapped column width without splitting a surrogate pair.
    /// </summary>
    /// <remarks>
    /// A title carrying an emoji stores that glyph as two chars. Cutting between them leaves a lone
    /// surrogate, which is not valid UTF-8: the insert then fails at the driver, and because
    /// extraction runs on a context shared by the whole run, that failure is not local to this row.
    /// </remarks>
    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        var cut = maxLength;
        if (char.IsHighSurrogate(value[cut - 1]))
            cut--;

        return value[..cut];
    }

    [GeneratedRegex(@"```json|```")]
    private static partial Regex StripCodeFences();

    private sealed record EventReplacement(TrackedEvent Replaced, TrackedEvent Replacement);

    /// <summary>
    /// What one pass over the model's event list produced: the rows to insert, the supersessions
    /// they declare, and whether an already-tracked row was reinstated in place.
    /// </summary>
    private sealed record ExtractionBatch(
        List<TrackedEvent> Created,
        List<EventReplacement> Replacements,
        bool Reinstated);
}
