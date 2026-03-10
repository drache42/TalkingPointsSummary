using System.Threading;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Provides cancellation-aware delays backed by a <see cref="TimeProvider"/> timer.
/// </summary>
internal static class TimeProviderDelay
{
    /// <summary>
    /// Waits for the specified duration or until cancellation is requested.
    /// </summary>
    public static Task DelayAsync(TimeProvider timeProvider, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = timeProvider.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            delay,
            Timeout.InfiniteTimeSpan);

        var registration = cancellationToken.Register(static state =>
        {
            var (taskCompletionSource, token) = ((TaskCompletionSource, CancellationToken))state!;
            taskCompletionSource.TrySetCanceled(token);
        }, (completion, cancellationToken));

        return AwaitDelayAsync(completion.Task, timer, registration);
    }

    private static async Task AwaitDelayAsync(Task delayTask, ITimer timer, CancellationTokenRegistration registration)
    {
        try
        {
            await delayTask;
        }
        finally
        {
            registration.Dispose();
            timer.Dispose();
        }
    }
}