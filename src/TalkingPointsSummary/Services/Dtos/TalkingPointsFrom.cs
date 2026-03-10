namespace TalkingPointsSummary.Services;

/// <summary>
/// Sender payload embedded in a TalkingPoints message.
/// </summary>
public class TalkingPointsFrom
{
    /// <summary>
    /// User details for the sender.
    /// </summary>
    public TalkingPointsUser? User { get; set; }
}
