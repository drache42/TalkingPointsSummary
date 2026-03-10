using System.ComponentModel.DataAnnotations;

namespace TalkingPointsSummary.Configuration;

public sealed class TalkingPointsApiOptions
{
    public const string SectionName = "TalkingPointsApi";

    [Range(1, 100)]
    public int MaxPagesPerRun { get; init; } = 3;
}