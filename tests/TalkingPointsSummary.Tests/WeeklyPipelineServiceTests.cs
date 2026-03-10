using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
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
            NullLogger<WeeklyPipelineService>.Instance);
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
        mockApiClient.Setup(x => x.FetchMessagesAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        services.AddSingleton(mockApiClient.Object);
        services.AddSingleton(Mock.Of<IMessageDeduplicator>());
        services.AddSingleton(Mock.Of<IMessageCategorizer>());
        services.AddSingleton(Mock.Of<INewsletterScraper>());
        services.AddSingleton(Mock.Of<ISummaryGenerator>());
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

    [Fact]
    public async Task TryRunFullPipelineAsync_AlreadyRunning_ReturnsAlreadyRunning()
    {
        var tcs = new TaskCompletionSource();
        var slowApiClient = new Mock<ITalkingPointsApiClient>();
        slowApiClient.Setup(x => x.FetchMessagesAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
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

        // Give it a moment to acquire the lock
        await Task.Delay(100);

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
    public async Task IsRunInProgress_TrueWhileRunning_FalseAfterComplete()
    {
        var tcs = new TaskCompletionSource();
        var slowApiClient = new Mock<ITalkingPointsApiClient>();
        slowApiClient.Setup(x => x.FetchMessagesAsync(It.IsAny<Parent>(), It.IsAny<CancellationToken>()))
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
        await Task.Delay(100);

        service.IsRunInProgress.Should().BeTrue();

        tcs.SetResult();
        await runTask;

        service.IsRunInProgress.Should().BeFalse();
    }

    public void Dispose()
    {
        // InMemory databases are cleaned up when the last connection closes
    }
}
