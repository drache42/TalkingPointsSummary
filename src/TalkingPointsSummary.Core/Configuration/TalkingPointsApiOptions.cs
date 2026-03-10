using System.ComponentModel.DataAnnotations;

namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Configuration values for paging requests to the TalkingPoints API.
/// </summary>
public sealed class TalkingPointsApiOptions
{
    /// <summary>
    /// Configuration section name for TalkingPoints API settings.
    /// </summary>
    public const string SectionName = "TalkingPointsApi";

    /// <summary>
    /// Maximum number of API pages fetched during a single run.
    /// </summary>
    [Range(1, 100)]
    public int MaxPagesPerRun { get; init; } = 3;
}