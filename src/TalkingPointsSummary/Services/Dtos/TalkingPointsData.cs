namespace TalkingPointsSummary.Services;

/// <summary>
/// Data payload returned by the TalkingPoints feed API.
/// </summary>
public class TalkingPointsData
{
    /// <summary>
    /// Messages returned for the requested page.
    /// </summary>
    public List<TalkingPointsMessage> Messages { get; set; } = [];
}
