using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;

namespace TalkingPointsSummary.Pipeline;

/// <summary>
/// Background service that runs the weekly pipeline on schedule.
/// Checks every minute whether it's time to run (default: Monday 8 AM).
/// </summary>
public class WeeklyPipelineService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WeeklyPipelineService> _logger;
    private readonly AppSettings _settings;
    private DateTime? _lastRunDate;

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

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTime.UtcNow;
                if (ShouldRun(now))
                {
                    _logger.LogInformation("Schedule triggered — starting weekly pipeline run");
                    await RunFullPipelineAsync(stoppingToken);
                    _lastRunDate = now.Date;
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
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<PipelineOrchestrator>();

        var parents = await db.Parents
            .Where(p => p.IsActive)
            .Include(p => p.Children)
            .ToListAsync(ct);

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
    }
}
