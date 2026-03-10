namespace TalkingPointsSummary.Services;

/// <summary>
/// Contact information embedded in a TalkingPoints message payload.
/// </summary>
public class TalkingPointsContactInfo
{
    /// <summary>
    /// Student name associated with the message.
    /// </summary>
    public string? StudentName { get; set; }
}
