using FluentAssertions;
using TalkingPointsSummary.Pipeline;

namespace TalkingPointsSummary.Tests;

public class WeeklyPipelineServiceTests
{
    [Theory]
    [InlineData(DayOfWeek.Monday, 8, 1, 8, true)]     // Monday 8 AM, schedule Monday 8 = should run
    [InlineData(DayOfWeek.Monday, 9, 1, 8, false)]    // Monday 9 AM, schedule Monday 8 = wrong hour
    [InlineData(DayOfWeek.Tuesday, 8, 1, 8, false)]   // Tuesday 8 AM, schedule Monday 8 = wrong day
    [InlineData(DayOfWeek.Sunday, 8, 0, 8, true)]     // Sunday 8 AM, schedule Sunday 8 = should run
    [InlineData(DayOfWeek.Monday, 8, 1, 9, false)]    // Monday 8 AM, schedule Monday 9 = wrong hour
    public void ShouldRun_RespectsSchedule(DayOfWeek dayOfWeek, int hour, int scheduledDay, int scheduledHour, bool expected)
    {
        // Find a date that falls on the specified day of week
        var baseDate = new DateTime(2026, 3, 2, hour, 30, 0, DateTimeKind.Utc); // March 2, 2026 is Monday
        while (baseDate.DayOfWeek != dayOfWeek)
        {
            baseDate = baseDate.AddDays(1);
        }
        baseDate = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, hour, 30, 0, DateTimeKind.Utc);

        var shouldRun = ShouldRunCheck(baseDate, scheduledDay, scheduledHour, lastRunDate: null);
        shouldRun.Should().Be(expected);
    }

    [Fact]
    public void ShouldRun_DoesNotRunTwiceSameDay()
    {
        var now = new DateTime(2026, 3, 2, 8, 30, 0, DateTimeKind.Utc); // Monday 8 AM
        var shouldRun = ShouldRunCheck(now, scheduledDay: 1, scheduledHour: 8, lastRunDate: now.Date);
        shouldRun.Should().BeFalse();
    }

    [Fact]
    public void ShouldRun_RunsOnNextWeek()
    {
        var now = new DateTime(2026, 3, 9, 8, 30, 0, DateTimeKind.Utc); // Next Monday 8 AM
        var lastRun = new DateTime(2026, 3, 2); // Last Monday
        var shouldRun = ShouldRunCheck(now, scheduledDay: 1, scheduledHour: 8, lastRunDate: lastRun);
        shouldRun.Should().BeTrue();
    }

    /// <summary>
    /// Mirrors the ShouldRun logic from WeeklyPipelineService for testability.
    /// </summary>
    private static bool ShouldRunCheck(DateTime now, int scheduledDay, int scheduledHour, DateTime? lastRunDate)
    {
        if ((int)now.DayOfWeek != scheduledDay)
            return false;

        if (now.Hour != scheduledHour)
            return false;

        if (lastRunDate.HasValue && lastRunDate.Value == now.Date)
            return false;

        return true;
    }
}
