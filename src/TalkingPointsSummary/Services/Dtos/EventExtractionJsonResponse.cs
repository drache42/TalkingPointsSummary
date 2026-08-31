using System.Text.Json.Serialization;

namespace TalkingPointsSummary.Services;

/// <summary>
/// JSON shape expected from the calendar event extraction model response.
/// </summary>
public class EventExtractionJsonResponse
{
    /// <summary>
    /// Dated events the model found in the news item.
    /// </summary>
    [JsonPropertyName("events")]
    public List<EventExtractionJsonEvent>? Events { get; set; }

    /// <summary>
    /// Identifiers of already-tracked events the news item cancels outright.
    /// </summary>
    [JsonPropertyName("cancelled_event_ids")]
    public List<int>? CancelledEventIds { get; set; }
}

/// <summary>
/// A single dated event returned by the calendar event extraction model.
/// </summary>
public class EventExtractionJsonEvent
{
    /// <summary>
    /// Short title naming the event.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Absolute date the event takes place, in YYYY-MM-DD form.
    /// </summary>
    [JsonPropertyName("event_date")]
    public string? EventDate { get; set; }

    /// <summary>
    /// Free-text time of day for the event, when one was announced.
    /// </summary>
    [JsonPropertyName("time_text")]
    public string? TimeText { get; set; }

    /// <summary>
    /// Identifier of an already-tracked event this entry replaces, when the news item
    /// moves or changes an event that was announced earlier.
    /// </summary>
    [JsonPropertyName("replaces_event_id")]
    public int? ReplacesEventId { get; set; }
}
