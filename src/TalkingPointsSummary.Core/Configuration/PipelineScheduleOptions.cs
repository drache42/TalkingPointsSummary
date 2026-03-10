using System.ComponentModel.DataAnnotations;

namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Configuration values that control the scheduled weekly pipeline run.
/// </summary>
public sealed class PipelineScheduleOptions
{
    /// <summary>
    /// Configuration section name for pipeline scheduling.
    /// </summary>
    public const string SectionName = "PipelineSchedule";

    /// <summary>
    /// Day of week for the scheduled run, where 0 is Sunday and 6 is Saturday.
    /// </summary>
    [Range(0, 6)]
    public int DayOfWeek { get; set; } = 1;

    /// <summary>
    /// Hour of day in 24-hour time when the scheduled run should start.
    /// </summary>
    [Range(0, 23)]
    public int Hour { get; set; } = 8;
}