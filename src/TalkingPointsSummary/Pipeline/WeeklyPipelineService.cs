using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Pipeline;

/// <summary>
/// Outcome returned when a pipeline run request finishes or is rejected.
/// </summary>
public enum PipelineRunStatus
{
    /// <summary>
    /// The pipeline run completed.
    /// </summary>
    Completed,

    /// <summary>
    /// The run was rejected because another run is already active.
    /// </summary>
    AlreadyRunning,

    /// <summary>
    /// The scheduled run was skipped because it was already recorded for the date.
    /// </summary>
    AlreadyScheduled,

    /// <summary>
    /// The requested parent was not found or is inactive.
    /// </summary>
    ParentNotFound
}

/// <summary>
/// Outcome returned when starting a background pipeline run.
/// </summary>
public enum PipelineStartStatus
{
    /// <summary>
    /// The background run was started.
    /// </summary>
    Started,

    /// <summary>
    /// The run was rejected because another run is already active.
    /// </summary>
    AlreadyRunning,

    /// <summary>
    /// The requested parent was not found or is inactive.
    /// </summary>
    ParentNotFound
}

/// <summary>
/// Background service that runs the weekly pipeline on schedule.
/// Waits for the next scheduled UTC run, retries within the scheduled hour when needed,
/// and then advances to the following week after a successful evaluation.
/// </summary>
public class WeeklyPipelineService : BackgroundService
{
    private static readonly TimeSpan ScheduledWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WeeklyPipelineService> _logger;
    private readonly PipelineScheduleOptions _schedule;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private int _isRunInProgress;
    private const string ScheduleTrigger = "schedule";

    /// <summary>
    /// Initializes the scheduled weekly pipeline background service.
    /// </summary>
    /// <param name="scopeFactory">Scope factory used to resolve per-run services.</param>
    /// <param name="schedule">Configured weekly schedule.</param>
    /// <param name="logger">Logger used for scheduler diagnostics.</param>
    /// <param name="timeProvider">Optional time provider used for scheduling decisions.</param>
    public WeeklyPipelineService(
        IServiceScopeFactory scopeFactory,
        IOptions<PipelineScheduleOptions> schedule,
        ILogger<WeeklyPipelineService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _schedule = schedule.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Executes the background scheduling loop.
    /// </summary>
    /// <param name="stoppingToken">Token that stops the service loop.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Weekly pipeline service started. Schedule: {Day} at {Hour}:00",
            (DayOfWeek)_schedule.DayOfWeek,
            _schedule.Hour);

        var scheduledWindowStartUtc = GetNextScheduledWindowStartUtc(_timeProvider.GetUtcDateTime());
        var nextWakeUpUtc = GetInitialWakeUpUtc(_timeProvider.GetUtcDateTime(), scheduledWindowStartUtc);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = _timeProvider.GetUtcDateTime();

                if (nextWakeUpUtc > now)
                {
                    _logger.LogInformation(
                        "Next scheduled pipeline evaluation for {ScheduledDate} at {WakeUpUtc}",
                        scheduledWindowStartUtc.Date,
                        nextWakeUpUtc);
                    await TimeProviderDelay.DelayAsync(_timeProvider, nextWakeUpUtc - now, stoppingToken);
                }

                _logger.LogInformation("Schedule triggered - evaluating weekly pipeline run for {Date}", scheduledWindowStartUtc.Date);
                var result = await TryRunScheduledPipelineAsync(scheduledWindowStartUtc, stoppingToken);
                if (result == PipelineRunStatus.Completed)
                {
                    scheduledWindowStartUtc = scheduledWindowStartUtc.AddDays(7);
                    nextWakeUpUtc = scheduledWindowStartUtc;
                    continue;
                }

                if (result == PipelineRunStatus.AlreadyScheduled)
                {
                    _logger.LogInformation("Scheduled pipeline run skipped because a run was already recorded for {Date}", scheduledWindowStartUtc.Date);
                    scheduledWindowStartUtc = scheduledWindowStartUtc.AddDays(7);
                    nextWakeUpUtc = scheduledWindowStartUtc;
                }
                else
                {
                    _logger.LogWarning("Scheduled pipeline run skipped because another run is already in progress");
                    nextWakeUpUtc = GetRetryWakeUpUtc(_timeProvider.GetUtcDateTime(), scheduledWindowStartUtc);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in weekly pipeline scheduler");
                nextWakeUpUtc = GetRetryWakeUpUtc(_timeProvider.GetUtcDateTime(), scheduledWindowStartUtc);
            }
        }

        _logger.LogInformation("Weekly pipeline service stopped");
    }

    internal bool ShouldRun(DateTime now)
    {
        if ((int)now.DayOfWeek != _schedule.DayOfWeek)
            return false;

        if (now.Hour != _schedule.Hour)
            return false;

        return true;
    }

    /// <summary>
    /// Returns the scheduled window start to evaluate next.
    /// If the worker starts during the scheduled hour, the current window is returned
    /// so the run can be evaluated immediately.
    /// </summary>
    internal DateTime GetNextScheduledWindowStartUtc(DateTime now)
    {
        if (ShouldRun(now))
        {
            return GetScheduledWindowStartUtc(now);
        }

        var daysUntilScheduled = (_schedule.DayOfWeek - (int)now.DayOfWeek + 7) % 7;
        var candidateDate = now.Date.AddDays(daysUntilScheduled);
        var candidate = new DateTime(
            candidateDate.Year,
            candidateDate.Month,
            candidateDate.Day,
            _schedule.Hour,
            0,
            0,
            DateTimeKind.Utc);

        if (candidate <= now)
        {
            candidate = candidate.AddDays(7);
        }

        return candidate;
    }

    /// <summary>
    /// Returns the next retry wake-up inside the current scheduled hour, or the next
    /// weekly scheduled window once the current hour has elapsed.
    /// </summary>
    internal DateTime GetRetryWakeUpUtc(DateTime now, DateTime scheduledWindowStartUtc)
    {
        var retryWakeUpUtc = now.Add(RetryInterval);
        var scheduledWindowEndUtc = scheduledWindowStartUtc.Add(ScheduledWindow);
        return retryWakeUpUtc < scheduledWindowEndUtc
            ? retryWakeUpUtc
            : scheduledWindowStartUtc.AddDays(7);
    }

    private static DateTime GetInitialWakeUpUtc(DateTime now, DateTime scheduledWindowStartUtc)
        => scheduledWindowStartUtc > now ? scheduledWindowStartUtc : now;

    private DateTime GetScheduledWindowStartUtc(DateTime now)
        => new(now.Year, now.Month, now.Day, _schedule.Hour, 0, 0, DateTimeKind.Utc);

    internal Task<PipelineRunStatus> TryRunScheduledPipelineAsync(DateTime now, CancellationToken ct = default)
        => TryRunPipelineAsync(ScheduleTrigger, parentId: null, scheduledDate: now.Date, ct);

    /// <summary>
    /// Runs the full pipeline for all active parents. Can be called manually via CLI.
    /// </summary>
    public async Task RunFullPipelineAsync(CancellationToken ct = default)
    {
        var result = await TryRunFullPipelineAsync("manual", parentId: null, ct);
        if (result == PipelineRunStatus.AlreadyRunning)
            throw new InvalidOperationException("A pipeline run is already in progress.");
        if (result == PipelineRunStatus.ParentNotFound)
            throw new InvalidOperationException("The requested parent was not found or is inactive.");
    }

    /// <summary>
    /// Returns whether a pipeline run is currently active.
    /// </summary>
    public bool IsRunInProgress => Volatile.Read(ref _isRunInProgress) == 1;

    /// <summary>
    /// Attempts to run the full pipeline for all active parents.
    /// </summary>
    /// <param name="trigger">Trigger name recorded for the run.</param>
    /// <param name="ct">Token used to cancel the run.</param>
    public async Task<PipelineRunStatus> TryRunFullPipelineAsync(string trigger, CancellationToken ct = default)
        => await TryRunPipelineAsync(trigger, parentId: null, scheduledDate: null, ct);

    /// <summary>
    /// Attempts to run the full pipeline, optionally scoped to a single parent.
    /// </summary>
    /// <param name="trigger">Trigger name recorded for the run.</param>
    /// <param name="parentId">Optional parent identifier to scope the run.</param>
    /// <param name="ct">Token used to cancel the run.</param>
    public async Task<PipelineRunStatus> TryRunFullPipelineAsync(string trigger, int? parentId, CancellationToken ct = default)
        => await TryRunPipelineAsync(trigger, parentId, scheduledDate: null, ct);

    private async Task<PipelineRunStatus> TryRunPipelineAsync(string trigger, int? parentId, DateTime? scheduledDate, CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(TimeSpan.Zero, ct))
            return PipelineRunStatus.AlreadyRunning;

        Interlocked.Exchange(ref _isRunInProgress, 1);
        int? pipelineRunId = null;

        try
        {
            if (scheduledDate.HasValue)
            {
                pipelineRunId = await TryCreateScheduledRunRecordAsync(trigger, scheduledDate.Value, ct);
                if (!pipelineRunId.HasValue)
                {
                    return PipelineRunStatus.AlreadyScheduled;
                }
            }

            _logger.LogInformation("Pipeline run started by {Trigger}{Scope}",
                trigger,
                parentId.HasValue ? $" for parent {parentId.Value}" : " for all active parents");

            var result = await RunPipelineCoreAsync(parentId, ct);

            if (pipelineRunId.HasValue)
            {
                await MarkPipelineRunCompletedAsync(pipelineRunId.Value, ct);
            }

            if (result == PipelineRunStatus.Completed)
            {
                _logger.LogInformation("Pipeline run started by {Trigger}{Scope} completed",
                    trigger,
                    parentId.HasValue ? $" for parent {parentId.Value}" : " for all active parents");
            }

            return result;
        }
        catch (Exception ex)
        {
            if (pipelineRunId.HasValue)
            {
                await MarkPipelineRunFailedAsync(pipelineRunId.Value, ex, CancellationToken.None);
            }

            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _isRunInProgress, 0);
            _runLock.Release();
        }
    }

    /// <summary>
    /// Attempts to start a background pipeline run and returns immediately.
    /// </summary>
    /// <param name="trigger">Trigger name recorded for the run.</param>
    /// <param name="parentId">Optional parent identifier to scope the run.</param>
    /// <param name="ct">Token used to cancel the start request.</param>
    public async Task<PipelineStartStatus> TryStartPipelineAsync(string trigger, int? parentId, CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(TimeSpan.Zero, ct))
            return PipelineStartStatus.AlreadyRunning;

        if (parentId.HasValue && !await ActiveParentExistsAsync(parentId.Value, ct))
        {
            _runLock.Release();
            return PipelineStartStatus.ParentNotFound;
        }

        Interlocked.Exchange(ref _isRunInProgress, 1);

        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Background pipeline run started by {Trigger}{Scope}",
                    trigger,
                    parentId.HasValue ? $" for parent {parentId.Value}" : " for all active parents");

                var result = await RunPipelineCoreAsync(parentId, CancellationToken.None);

                if (result == PipelineRunStatus.Completed)
                {
                    _logger.LogInformation("Background pipeline run started by {Trigger}{Scope} completed",
                        trigger,
                        parentId.HasValue ? $" for parent {parentId.Value}" : " for all active parents");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background pipeline run started by {Trigger}{Scope} failed",
                    trigger,
                    parentId.HasValue ? $" for parent {parentId.Value}" : " for all active parents");
            }
            finally
            {
                Interlocked.Exchange(ref _isRunInProgress, 0);
                _runLock.Release();
            }
        });

        return PipelineStartStatus.Started;
    }

    private async Task<bool> ActiveParentExistsAsync(int parentId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Parents.AnyAsync(parent => parent.IsActive && parent.Id == parentId, ct);
    }

    private async Task<int?> TryCreateScheduledRunRecordAsync(string trigger, DateTime scheduledDate, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existingRun = await db.PipelineRuns
            .AnyAsync(run => run.Trigger == trigger && run.ScheduledDate == scheduledDate, ct);
        if (existingRun)
        {
            return null;
        }

        var run = new PipelineRun
        {
            Trigger = trigger,
            ScheduledDate = scheduledDate,
            StartedAt = _timeProvider.GetUtcDateTime(),
            Status = PipelineRunRecordStatus.Started
        };

        db.PipelineRuns.Add(run);

        try
        {
            await db.SaveChangesAsync(ct);
            return run.Id;
        }
        catch (DbUpdateException)
        {
            return null;
        }
    }

    private async Task MarkPipelineRunCompletedAsync(int pipelineRunId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.PipelineRuns.FindAsync([pipelineRunId], ct);
        if (run is null)
        {
            return;
        }

        run.CompletedAt = _timeProvider.GetUtcDateTime();
        run.Status = PipelineRunRecordStatus.Completed;
        run.Error = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkPipelineRunFailedAsync(int pipelineRunId, Exception ex, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.PipelineRuns.FindAsync([pipelineRunId], ct);
        if (run is null)
        {
            return;
        }

        run.CompletedAt = _timeProvider.GetUtcDateTime();
        run.Status = PipelineRunRecordStatus.Failed;
        run.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        await db.SaveChangesAsync(ct);
    }

    private async Task<PipelineRunStatus> RunPipelineCoreAsync(int? parentId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<PipelineOrchestrator>();

        var parentQuery = db.Parents
            .Where(p => p.IsActive)
            .Include(p => p.Children)
            .AsQueryable();

        if (parentId.HasValue)
        {
            parentQuery = parentQuery.Where(p => p.Id == parentId.Value);
        }

        var parents = await parentQuery.ToListAsync(ct);

        if (parentId.HasValue && parents.Count == 0)
        {
            _logger.LogWarning("Requested pipeline run for parent {ParentId}, but no active parent was found", parentId.Value);
            return PipelineRunStatus.ParentNotFound;
        }

        _logger.LogInformation("Running pipeline for {Count} active parent(s)", parents.Count);

        foreach (var parent in parents)
        {
            try
            {
                await orchestrator.RunAsync(parent, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline failed for parent {ParentName}, continuing with next parent",
                    parent.Name);
            }
        }

        _logger.LogInformation("Weekly pipeline run complete");
        return PipelineRunStatus.Completed;
    }
}
