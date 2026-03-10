using System.ComponentModel.DataAnnotations;

namespace TalkingPointsSummary.Configuration;

public sealed class PipelineScheduleOptions
{
    public const string SectionName = "PipelineSchedule";

    [Range(0, 6)]
    public int DayOfWeek { get; set; } = 1;

    [Range(0, 23)]
    public int Hour { get; set; } = 8;
}