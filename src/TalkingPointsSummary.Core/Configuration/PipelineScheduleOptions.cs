using System.ComponentModel.DataAnnotations;

namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Configuration values that control the scheduled weekly pipeline run.
/// </summary>
public sealed class PipelineScheduleOptions : IValidatableObject
{
    /// <summary>
    /// Configuration section name for pipeline scheduling.
    /// </summary>
    public const string SectionName = "PipelineSchedule";

    /// <summary>
    /// Day of week for the scheduled run, where 0 is Sunday and 6 is Saturday.
    /// Interpreted in <see cref="TimeZone"/> when set, otherwise UTC.
    /// </summary>
    [Range(0, 6)]
    public int DayOfWeek { get; set; } = 1;

    /// <summary>
    /// Hour of day in 24-hour time when the scheduled run should start.
    /// Interpreted in <see cref="TimeZone"/> when set, otherwise UTC.
    /// </summary>
    [Range(0, 23)]
    public int Hour { get; set; } = 8;

    /// <summary>
    /// Timezone identifier for the schedule. Defaults to <c>UTC</c>.
    /// Accepts IANA format (e.g. <c>America/New_York</c>) or Windows format (e.g. <c>Eastern Standard Time</c>).
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <inheritdoc/>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var valid = true;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            valid = false;
        }

        if (!valid)
        {
            yield return new ValidationResult(
                $"PipelineSchedule:TimeZone '{TimeZone}' is not a recognised timezone identifier.",
                [nameof(TimeZone)]);
        }
    }
}