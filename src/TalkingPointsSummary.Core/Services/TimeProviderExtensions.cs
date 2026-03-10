namespace TalkingPointsSummary.Services;

public static class TimeProviderExtensions
{
    public static DateTime GetUtcDateTime(this TimeProvider timeProvider)
        => timeProvider.GetUtcNow().UtcDateTime;
}
