namespace TalkingPointsSummary.Services;

/// <summary>
/// User details embedded in a TalkingPoints sender payload.
/// </summary>
public class TalkingPointsUser
{
    /// <summary>
    /// Signature or display name for the user.
    /// </summary>
    public string? Signature { get; set; }
}
