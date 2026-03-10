namespace TalkingPointsSummary.Services;

/// <summary>
/// Root response payload returned by the TalkingPoints feed API.
/// </summary>
public class TalkingPointsApiResponse
{
    /// <summary>
    /// Response data payload containing paged messages.
    /// </summary>
    public TalkingPointsData? Data { get; set; }
}
