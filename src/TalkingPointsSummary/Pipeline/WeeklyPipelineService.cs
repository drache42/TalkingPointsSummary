using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;

namespace TalkingPointsSummary.Pipeline;

public enum PipelineRunStatus
{
    Completed,
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
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private DateTime? _lastRunDate;
    private int _isRunInProgress;

    public WeeklyPipelineService(
        IServiceScopeFactory scopeFactory,
        IOptions<AppSettings> settings,
        ILogger<WeeklyPipelineService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Weekly pipeline service started. Schedule: {Day} at {Hour}:00",
            (DayOfWeek)_settings.ScheduleDayOfWeek,
            _settings.ScheduleHour);

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
                    var result = await TryRunFullPipelineAsync("schedule", stoppingToken);
                    if (result == PipelineRunStatus.Completed)
                    {
                        _lastRunDate = now.Date;
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

    private bool ShouldRun(DateTime now)
    {
        // Check day of week and hour
        if ((int)now.DayOfWeek != _settings.ScheduleDayOfWeek)
            return false;

        if (now.Hour != _settings.ScheduleHour)
            return false;

        // Don't run more than once per day
        if (_lastRunDate.HasValue && _lastRunDate.Value == now.Date)
            return false;

        return true;
    }

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
        => await TryRunFullPipelineAsync(trigger, parentId: null, ct);

    public async Task<PipelineRunStatus> TryRunFullPipelineAsync(string trigger, int? parentId, CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(TimeSpan.Zero, ct))
            return PipelineRunStatus.AlreadyRunning;

        Interlocked.Exchange(ref _isRunInProgress, 1);

        try
        {
            _logger.LogInformation("Pipeline run started by {Trigger}{Scope}",
                trigger,
                parentId.HasValue ? $" for parent {parentId.Value}" : " for all active parents");

            var result = await RunPipelineCoreAsync(parentId, ct);

            if (result == PipelineRunStatus.Completed)
            {
                _logger.LogInformation("Pipeline run started by {Trigger}{Scope} completed",
                    trigger,
                    parentId.HasValue ? $" for parent {parentId.Value}" : " for all active parents");
            }

            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _isRunInProgress, 0);
            _runLock.Release();
        }
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
