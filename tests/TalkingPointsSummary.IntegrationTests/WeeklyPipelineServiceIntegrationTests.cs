using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.IntegrationTests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public class WeeklyPipelineServiceIntegrationTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public WeeklyPipelineServiceIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TryRunFullPipeline_ConcurrentCalls_SecondReturnsAlreadyRunning()
    {
        // Arrange: stub with delay so first run takes a while
        _fixture.StubTalkingPointsApi(new List<TalkingPointsMessage>
        {
            new()
            {
                Id = "concurrent-1",
                Text = "Test message",
                FromName = "Teacher",
                From = new TalkingPointsFrom { User = new TalkingPointsUser { Signature = "Teacher" } },
                ContactInfo = new TalkingPointsContactInfo { StudentName = "Alice" },
                CreatedAt = DateTime.UtcNow,
                DisplayDate = DateTime.UtcNow,
            }
        });

        // Anthropic categorization with 500ms delay, then summary
        _fixture.StubAnthropicCategorizationWithDelay(
            """{"message_id":"concurrent-1","has_newsletter_url":false,"is_news_itself":true,"summary":"Test"}""",
            500);
        _fixture.StubAnthropicSummary("# Summary\n\nTest");

        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();

        // Act: start the first run, then wait until the observable run state flips.
        var firstRun = pipeline.TryRunFullPipelineAsync("test-1", _fixture.SeededParentId, CancellationToken.None);
        await WaitForConditionAsync(() => pipeline.IsRunInProgress, TimeSpan.FromSeconds(5));

        // Second call should return AlreadyRunning
        var secondResult = await pipeline.TryRunFullPipelineAsync("test-2", _fixture.SeededParentId, CancellationToken.None);

        // Assert
        secondResult.Should().Be(PipelineRunStatus.AlreadyRunning);

        // Await first task to complete
        var firstResult = await firstRun;
        firstResult.Should().Be(PipelineRunStatus.Completed);
    }

    [Fact]
    public async Task TryRunFullPipeline_InactiveParentId_ReturnsParentNotFound()
    {
        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();

        var result = await pipeline.TryRunFullPipelineAsync("test", 99999, CancellationToken.None);

        result.Should().Be(PipelineRunStatus.ParentNotFound);
    }

    [Fact]
    public async Task IsRunInProgress_ReflectsRunState()
    {
        // Arrange
        _fixture.StubTalkingPointsApi(new List<TalkingPointsMessage>
        {
            new()
            {
                Id = "progress-1",
                Text = "Progress test",
                FromName = "Teacher",
                From = new TalkingPointsFrom { User = new TalkingPointsUser { Signature = "Teacher" } },
                ContactInfo = new TalkingPointsContactInfo { StudentName = "Alice" },
                CreatedAt = DateTime.UtcNow,
                DisplayDate = DateTime.UtcNow,
            }
        });

        _fixture.StubAnthropicCategorizationWithDelay(
            """{"message_id":"progress-1","has_newsletter_url":false,"is_news_itself":true,"summary":"Test"}""",
            500);
        _fixture.StubAnthropicSummary("# Summary\n\nTest");

        await using var sp = _fixture.CreateServiceProvider();
        var pipeline = sp.GetRequiredService<WeeklyPipelineService>();

        // Before any run
        pipeline.IsRunInProgress.Should().BeFalse();

        // Start run and wait until the public run-state flag flips.
        var runTask = pipeline.TryRunFullPipelineAsync("test", _fixture.SeededParentId, CancellationToken.None);
        await WaitForConditionAsync(() => pipeline.IsRunInProgress, TimeSpan.FromSeconds(5));

        // During run
        pipeline.IsRunInProgress.Should().BeTrue();

        // After run completes
        await runTask;
        await WaitForConditionAsync(() => !pipeline.IsRunInProgress, TimeSpan.FromSeconds(5));
        pipeline.IsRunInProgress.Should().BeFalse();
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

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for pipeline state transition.");
    }
}
