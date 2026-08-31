namespace TalkingPointsSummary.Models;

/// <summary>
/// Lifecycle state of a tracked school event.
/// </summary>
public enum TrackedEventStatus
{
    /// <summary>
    /// The event is current and should still be surfaced to parents.
    /// </summary>
    Active,

    /// <summary>
    /// A later announcement replaced this event, for example with a new date or time.
    /// </summary>
    Superseded,

    /// <summary>
    /// The event was cancelled by the school.
    /// </summary>
    Cancelled
}

/// <summary>
/// A dated school event extracted from a news item and tracked across weekly digests.
/// </summary>
public class TrackedEvent
{
    /// <summary>
    /// Database identifier for the tracked event.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Identifier of the parent that owns the tracked event.
    /// </summary>
    public int ParentId { get; set; }

    /// <summary>
    /// Identifier of the news item the event was extracted from.
    /// </summary>
    public int SourceNewsItemId { get; set; }

    /// <summary>
    /// School the event belongs to.
    /// </summary>
    public string School { get; set; } = string.Empty;

    /// <summary>
    /// Date the event takes place.
    /// </summary>
    public DateTime EventDate { get; set; }

    /// <summary>
    /// Short title describing the event.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Free-text time of day for the event, such as "6:30 PM", when one was announced.
    /// </summary>
    public string? TimeText { get; set; }

    /// <summary>
    /// Current lifecycle state of the event.
    /// </summary>
    public TrackedEventStatus Status { get; set; } = TrackedEventStatus.Active;

    /// <summary>
    /// Identifier of the tracked event that replaced this one, when the status is
    /// <see cref="TrackedEventStatus.Superseded"/>.
    /// </summary>
    public int? SupersededByEventId { get; set; }

    /// <summary>
    /// UTC time when the tracked event was created locally.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Parent that owns the tracked event.
    /// </summary>
    public Parent Parent { get; set; } = null!;

    /// <summary>
    /// News item the event was extracted from.
    /// </summary>
    public NewsItem NewsItem { get; set; } = null!;
}
