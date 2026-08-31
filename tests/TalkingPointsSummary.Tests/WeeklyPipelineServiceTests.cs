using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Diagnostics;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class WeeklyPipelineServiceTests : IDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly PipelineScheduleOptions _settings;
    private readonly FixedTimeProvider _timeProvider = new(new DateTimeOffset(2026, 3, 2, 8, 30, 0, TimeSpan.Zero));

    public WeeklyPipelineServiceTests()
    {
        _settings = new PipelineScheduleOptions
        {
            DayOfWeek = 1, // Monday
            Hour = 8
        };
    }

    private WeeklyPipelineService CreateService(IServiceScopeFactory? scopeFactory = null)
    {
        scopeFactory ??= CreateScopeFactory();
        return new WeeklyPipelineService(
            scopeFactory,
            Options.Create(_settings),
            NullLogger<WeeklyPipelineService>.Instance,
            _timeProvider);
    }

    private IServiceScopeFactory CreateScopeFactory(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase(_dbName));

        // Register mock service dependencies for PipelineOrchestrator
        services.AddSingleton(Options.Create(new PipelineScheduleOptions
        {
            DayOfWeek = _settings.DayOfWeek,
            Hour = _settings.Hour,
        }));
        var mockApiClient = new Mock<ITalkingPointsApiClient>();
        mockApiClient.Setup(x => x.FetchMessagesAsync(It.IsAny<Parent>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        services.AddSingleton(mockApiClient.Object);
        services.AddSingleton(Mock.Of<IMessageDeduplicator>());
        services.AddSingleton(Mock.Of<IMessageCategorizer>());
        services.AddSingleton(Mock.Of<INewsletterScraper>());
        services.AddSingleton(Mock.Of<IEventExtractor>());
        services.AddSingleton(Mock.Of<ISummaryGenerator>());
        services.AddSingleton<SummaryOutputValidator>();
        services.AddSingleton(Mock.Of<ISummaryCritic>());
        services.AddSingleton(Mock.Of<IMarkdownConverter>());
        services.AddSingleton(Mock.Of<IEmailSender>());
        services.AddSingleton<ILogger<PipelineOrchestrator>>(NullLogger<PipelineOrchestrator>.Instance);
        services.AddScoped<PipelineOrchestrator>();

        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, 8, 1, 8, true)]     // Monday 8 AM, schedule Monday 8 = should run
    [InlineData(DayOfWeek.Monday, 9, 1, 8, false)]    // Monday 9 AM, schedule Monday 8 = wrong hour
    [InlineData(DayOfWeek.Tuesday, 8, 1, 8, false)]   // Tuesday 8 AM, schedule Monday 8 = wrong day
    [InlineData(DayOfWeek.Sunday, 8, 0, 8, true)]     // Sunday 8 AM, schedule Sunday 8 = should run
    [InlineData(DayOfWeek.Monday, 8, 1, 9, false)]    // Monday 8 AM, schedule Monday 9 = wrong hour
    public void ShouldRun_RespectsSchedule(DayOfWeek dayOfWeek, int hour, int scheduledDay, int scheduledHour, bool expected)
    {
        _settings.DayOfWeek = scheduledDay;
        _settings.Hour = scheduledHour;
        var service = CreateService();

        // Find a date that falls on the specified day of week
        var baseDate = new DateTime(2026, 3, 2, hour, 30, 0, DateTimeKind.Utc); // March 2, 2026 is Monday
        while (baseDate.DayOfWeek != dayOfWeek)
        {
            baseDate = baseDate.AddDays(1);
        }
        baseDate = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, hour, 30, 0, DateTimeKind.Utc);

        var shouldRun = service.ShouldRun(baseDate);
        shouldRun.Should().Be(expected);
    }

    [Theory]
    [InlineData("2026-03-02T07:30:00Z", "2026-03-02T08:00:00Z")]
    [InlineData("2026-03-02T08:30:00Z", "2026-03-02T08:00:00Z")]
    [InlineData("2026-03-02T09:00:00Z", "2026-03-09T08:00:00Z")]
    [InlineData("2026-03-01T10:00:00Z", "2026-03-02T08:00:00Z")]
    public void GetNextScheduledWindowStartUtc_ReturnsExpectedOccurrence(string nowIso, string expectedIso)
    {
        var service = CreateService();

        var nextScheduledUtc = service.GetNextScheduledWindowStartUtc(DateTime.Parse(nowIso, null, System.Globalization.DateTimeStyles.RoundtripKind));

        nextScheduledUtc.Should().Be(DateTime.Parse(expectedIso, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, 13, 1, 8, "America/New_York", true)]   // Mon 13:00 UTC = Mon 08:00 EST → run
    [InlineData(DayOfWeek.Monday, 12, 1, 8, "America/New_York", false)]  // Mon 12:00 UTC = Mon 07:00 EST → wrong hour
    [InlineData(DayOfWeek.Tuesday, 13, 1, 8, "America/New_York", false)] // Tue 13:00 UTC = Tue 08:00 EST → wrong day
    public void ShouldRun_RespectsTimezone(DayOfWeek dayOfWeek, int hourUtc, int scheduledDay, int scheduledHour, string timezone, bool expected)
    {
        _settings.DayOfWeek = scheduledDay;
        _settings.Hour = scheduledHour;
        _settings.TimeZone = timezone;
        var service = CreateService();

        var baseDate = new DateTime(2026, 3, 2, hourUtc, 0, 0, DateTimeKind.Utc); // March 2, 2026 is Monday
        while (baseDate.DayOfWeek != dayOfWeek)
        {
            baseDate = baseDate.AddDays(1);
        }
        baseDate = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, hourUtc, 0, 0, DateTimeKind.Utc);

        service.ShouldRun(baseDate).Should().Be(expected);
    }

    [Theory]
    // Sunday before Monday 8am EST (UTC-5 in winter) → Mon 13:00 UTC
    [InlineData("2026-03-01T10:00:00Z", "2026-03-02T13:00:00Z")]
    // After Monday 8am EST, same week → next Monday is 2026-03-09 (EDT = UTC-4 after spring-forward on Mar 8) → Mon 12:00 UTC
    [InlineData("2026-03-02T14:30:00Z", "2026-03-09T12:00:00Z")]
    public void GetNextScheduledWindowStartUtc_RespectsTimezone(string nowIso, string expectedIso)
    {
        _settings.TimeZone = "America/New_York";
        var service = CreateService();

        var nextScheduledUtc = service.GetNextScheduledWindowStartUtc(DateTime.Parse(nowIso, null, System.Globalization.DateTimeStyles.RoundtripKind));

        nextScheduledUtc.Should().Be(DateTime.Parse(expectedIso, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Theory]
    [InlineData("2026-03-02T08:15:00Z", "2026-03-02T08:00:00Z", "2026-03-02T08:16:00Z")]
    [InlineData("2026-03-02T08:59:30Z", "2026-03-02T08:00:00Z", "2026-03-09T08:00:00Z")]
    public void GetRetryWakeUpUtc_RetriesWithinWindowOrAdvancesNextWeek(string nowIso, string scheduledStartIso, string expectedIso)
    {
        var service = CreateService();

        var retryWakeUpUtc = service.GetRetryWakeUpUtc(
            DateTime.Parse(nowIso, null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTime.Parse(scheduledStartIso, null, System.Globalization.DateTimeStyles.RoundtripKind));

        retryWakeUpUtc.Should().Be(DateTime.Parse(expectedIso, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public async Task TryRunFullPipelineAsync_AlreadyRunning_ReturnsAlreadyRunning()
    {
        var tcs = new TaskCompletionSource();
        var slowApiClient = new Mock<ITalkingPointsApiClient>();
        slowApiClient.Setup(x => x.FetchMessagesAsync(It.IsAny<Parent>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await tcs.Task;
                return new List<TalkingPointsMessage>();
            });

        var scopeFactory = CreateScopeFactory(services =>
        {
            // Replace the mock with a slow one
            services.AddSingleton(slowApiClient.Object);
        });

        // Seed an active parent so the pipeline actually calls the orchestrator
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Parents.Add(new Parent
            {
                Name = "Test", TalkingPointsToken = "t", TalkingPointsContactId = "c",
                EmailRecipients = "e@e.com", IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(scopeFactory);

        // Start first run (will block on slowApiClient)
        var firstRun = service.TryRunFullPipelineAsync("test");

        await WaitForConditionAsync(() => service.IsRunInProgress, TimeSpan.FromSeconds(1));

        // Second call should return AlreadyRunning
        var secondResult = await service.TryRunFullPipelineAsync("test");
        secondResult.Should().Be(PipelineRunStatus.AlreadyRunning);

        // Clean up - release the first run
        tcs.SetResult();
        var firstResult = await firstRun;
        firstResult.Should().Be(PipelineRunStatus.Completed);
    }

    [Fact]
    public async Task TryRunFullPipelineAsync_ParentNotFound_ReturnsParentNotFound()
    {
        var scopeFactory = CreateScopeFactory();

        // No parents seeded → parentId 999 should not be found
        var service = CreateService(scopeFactory);
        var result = await service.TryRunFullPipelineAsync("test", parentId: 999, CancellationToken.None);

        result.Should().Be(PipelineRunStatus.ParentNotFound);
    }

    [Fact]
    public async Task TryStartPipelineAsync_ParentNotFound_ReturnsParentNotFound()
    {
        var service = CreateService(CreateScopeFactory());

        var result = await service.TryStartPipelineAsync("test", parentId: 999, CancellationToken.None);

        result.Should().Be(PipelineStartStatus.ParentNotFound);
        service.IsRunInProgress.Should().BeFalse();
    }

    [Fact]
    public async Task TryStartPipelineAsync_AlreadyRunning_ReturnsAlreadyRunning()
    {
        var tcs = new TaskCompletionSource();
        var slowApiClient = new Mock<ITalkingPointsApiClient>();
        slowApiClient.Setup(x => x.FetchMessagesAsync(It.IsAny<Parent>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await tcs.Task;
                return new List<TalkingPointsMessage>();
            });

        var scopeFactory = CreateScopeFactory(services =>
        {
            services.AddSingleton(slowApiClient.Object);
        });

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Parents.Add(new Parent
            {
                Name = "Test", TalkingPointsToken = "t", TalkingPointsContactId = "c",
                EmailRecipients = "e@e.com", IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(scopeFactory);
        var firstResult = await service.TryStartPipelineAsync("test", parentId: null, CancellationToken.None);

        firstResult.Should().Be(PipelineStartStatus.Started);
        service.IsRunInProgress.Should().BeTrue();

        var secondResult = await service.TryStartPipelineAsync("test", parentId: null, CancellationToken.None);
        secondResult.Should().Be(PipelineStartStatus.AlreadyRunning);

        tcs.SetResult();

        await WaitForConditionAsync(() => !service.IsRunInProgress, TimeSpan.FromSeconds(1));

        service.IsRunInProgress.Should().BeFalse();
    }

    [Fact]
    public async Task IsRunInProgress_TrueWhileRunning_FalseAfterComplete()
    {
        var tcs = new TaskCompletionSource();
        var slowApiClient = new Mock<ITalkingPointsApiClient>();
        slowApiClient.Setup(x => x.FetchMessagesAsync(It.IsAny<Parent>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await tcs.Task;
                return new List<TalkingPointsMessage>();
            });

        var scopeFactory = CreateScopeFactory(services =>
        {
            services.AddSingleton(slowApiClient.Object);
        });

        // Seed parent
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Parents.Add(new Parent
            {
                Name = "Test", TalkingPointsToken = "t", TalkingPointsContactId = "c",
                EmailRecipients = "e@e.com", IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(scopeFactory);

        service.IsRunInProgress.Should().BeFalse();

        var runTask = service.TryRunFullPipelineAsync("test");
        await WaitForConditionAsync(() => service.IsRunInProgress, TimeSpan.FromSeconds(1));

        service.IsRunInProgress.Should().BeTrue();

        tcs.SetResult();
        await runTask;

        service.IsRunInProgress.Should().BeFalse();
    }

    [Fact]
    public async Task TryRunScheduledPipelineAsync_WhenRunAlreadyRecorded_ReturnsAlreadyScheduled()
    {
        var scopeFactory = CreateScopeFactory();
        var scheduledAt = new DateTime(2026, 3, 2, 8, 30, 0, DateTimeKind.Utc);

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PipelineRuns.Add(new PipelineRun
            {
                Trigger = "schedule",
                ScheduledDate = scheduledAt.Date,
                StartedAt = scheduledAt,
                CompletedAt = scheduledAt.AddMinutes(1),
                Status = PipelineRunRecordStatus.Completed
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(scopeFactory);

        var result = await service.TryRunScheduledPipelineAsync(scheduledAt, CancellationToken.None);

        result.Should().Be(PipelineRunStatus.AlreadyScheduled);
    }

    [Fact]
    public async Task TryRunScheduledPipelineAsync_PersistsRunRecordEvenWhenNoSummaryProduced()
    {
        var scopeFactory = CreateScopeFactory();
        var scheduledAt = new DateTime(2026, 3, 2, 8, 30, 0, DateTimeKind.Utc);

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Parents.Add(new Parent
            {
                Name = "Test",
                TalkingPointsToken = "t",
                TalkingPointsContactId = "c",
                EmailRecipients = "e@e.com",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(scopeFactory);

        var result = await service.TryRunScheduledPipelineAsync(scheduledAt, CancellationToken.None);

        result.Should().Be(PipelineRunStatus.Completed);

        using var assertScope = scopeFactory.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await assertDb.PipelineRuns.SingleAsync();
        run.Trigger.Should().Be("schedule");
        run.ScheduledDate.Should().Be(scheduledAt.Date);
        run.StartedAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);
        run.Status.Should().Be(PipelineRunRecordStatus.Completed);
        run.CompletedAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task TryRunScheduledPipelineAsync_AfterManualRun_StillRunsSchedule()
    {
        var scopeFactory = CreateScopeFactory();
        var scheduledAt = new DateTime(2026, 3, 2, 8, 30, 0, DateTimeKind.Utc);

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Parents.Add(new Parent
            {
                Name = "Test",
                TalkingPointsToken = "t",
                TalkingPointsContactId = "c",
                EmailRecipients = "e@e.com",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(scopeFactory);

        var manualResult = await service.TryRunFullPipelineAsync("manual", CancellationToken.None);
        var scheduledResult = await service.TryRunScheduledPipelineAsync(scheduledAt, CancellationToken.None);

        manualResult.Should().Be(PipelineRunStatus.Completed);
        scheduledResult.Should().Be(PipelineRunStatus.Completed);

        using var assertScope = scopeFactory.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        assertDb.PipelineRuns.Should().ContainSingle(run => run.Trigger == "schedule");
    }

    public void Dispose()
    {
        // InMemory databases are cleaned up when the last connection closes
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }

            await Task.Yield();
        }

        throw new TimeoutException("Timed out waiting for the pipeline state transition.");
    }
}
