namespace TalkingPointsSummary.Services;

/// <summary>
/// Convenience helpers for working with <see cref="TimeProvider"/> values.
/// </summary>
public static class TimeProviderExtensions
{
    /// <summary>
    /// Returns the current UTC time as a <see cref="DateTime"/>.
    /// </summary>
    /// <param name="timeProvider">The time provider used to obtain the current time.</param>
    public static DateTime GetUtcDateTime(this TimeProvider timeProvider)
        => timeProvider.GetUtcNow().UtcDateTime;
}
