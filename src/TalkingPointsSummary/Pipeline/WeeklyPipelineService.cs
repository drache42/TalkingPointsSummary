using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Models;

namespace TalkingPointsSummary.Pipeline;

public enum PipelineRunStatus
{
    Completed,
    AlreadyRunning,
    AlreadyScheduled,
    ParentNotFound
}

public enum PipelineStartStatus
{
    Started,
    AlreadyRunning,
    ParentNotFound
}

/// <summary>
/// Background service that runs the weekly pipeline on schedule.
/// Checks every minute whether it's time to run (default: Monday 8 AM).
/// </summary>
public class WeeklyPipelineService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WeeklyPipelineService> _logger;
    private readonly PipelineScheduleOptions _schedule;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private int _isRunInProgress;
    private const string ScheduleTrigger = "schedule";

    public WeeklyPipelineService(
        IServiceScopeFactory scopeFactory,
        IOptions<PipelineScheduleOptions> schedule,
        ILogger<WeeklyPipelineService> logger)
    {
        _scopeFactory = scopeFactory;
        _schedule = schedule.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Weekly pipeline service started. Schedule: {Day} at {Hour}:00",
            (DayOfWeek)_schedule.DayOfWeek,
            _schedule.Hour);

        // Gate: do not start the scheduler until migrations are fully applied.
        // This makes the service self-resilient if somehow ExecuteAsync starts before
        // Program.cs has finished applying migrations.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var pending = (await db.Database.GetPendingMigrationsAsync(stoppingToken)).ToList();
                if (pending.Count == 0)
                    break;
                _logger.LogWarning("Database has {Count} pending migration(s), waiting before starting scheduler", pending.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Database not ready, waiting before starting scheduler");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTime.UtcNow;
                if (ShouldRun(now))
                {
                    _logger.LogInformation("Schedule triggered - evaluating weekly pipeline run");
                    var result = await TryRunScheduledPipelineAsync(now, stoppingToken);
                    if (result == PipelineRunStatus.Completed)
                    {
                        continue;
                    }

                    if (result == PipelineRunStatus.AlreadyScheduled)
                    {
                        _logger.LogInformation("Scheduled pipeline run skipped because a run was already recorded for {Date}", now.Date);
                    }
                    else
                    {
                        _logger.LogWarning("Scheduled pipeline run skipped because another run is already in progress");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in weekly pipeline scheduler");
            }
        }

        _logger.LogInformation("Weekly pipeline service stopped");
    }

    internal bool ShouldRun(DateTime now)
    {
        // Check day of week and hour
        if ((int)now.DayOfWeek != _schedule.DayOfWeek)
            return false;

        if (now.Hour != _schedule.Hour)
            return false;

        return true;
    }

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

    public bool IsRunInProgress => Volatile.Read(ref _isRunInProgress) == 1;

    public async Task<PipelineRunStatus> TryRunFullPipelineAsync(string trigger, CancellationToken ct = default)
        => await TryRunPipelineAsync(trigger, parentId: null, scheduledDate: null, ct);

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
            StartedAt = DateTime.UtcNow,
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

        run.CompletedAt = DateTime.UtcNow;
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

        run.CompletedAt = DateTime.UtcNow;
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
