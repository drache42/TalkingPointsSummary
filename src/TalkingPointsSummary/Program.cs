using System.CommandLine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using TalkingPointsSummary.Commands;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary;

internal sealed class Program
{
    private const string ConsoleOutputTemplate = "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
    private const string FileOutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
    private const string EfCommandSourceContext = "Microsoft.EntityFrameworkCore.Database.Command";

    private sealed record PipelineRunRequest(int? ParentId);

    public static async Task Main(string[] args)
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environments.Production;

        var configuration = BuildConfiguration(environmentName);
        var debugFeaturesEnabled = DebugFeaturesOptions.IsEnabled(configuration);

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .WriteTo.Logger(logger => logger
                .Filter.ByExcluding(IsNoisyEfCommandLog)
                .WriteTo.Console(
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    outputTemplate: ConsoleOutputTemplate))
            .WriteTo.File(
                path: "logs/app-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: FileOutputTemplate)
            .CreateLogger();

        try
        {
            if (args.Length > 0)
            {
                await RunCliAsync(args, configuration);
            }
            else if (debugFeaturesEnabled)
            {
                await RunDebugWorkerAsync(args, environmentName, configuration);
            }
            else
            {
                await RunWorkerAsync(configuration);
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static IConfiguration BuildConfiguration(string environmentName)
        => WorkerConfiguration.BuildConfiguration(Directory.GetCurrentDirectory(), environmentName);

    private static bool IsNoisyEfCommandLog(Serilog.Events.LogEvent logEvent)
    {
        if (logEvent.Level >= LogEventLevel.Warning)
        {
            return false;
        }

        if (!logEvent.Properties.TryGetValue("SourceContext", out var sourceContext))
        {
            return false;
        }

        return string.Equals(sourceContext.ToString().Trim('"'), EfCommandSourceContext, StringComparison.Ordinal);
    }

    private static async Task RunCliAsync(string[] args, IConfiguration configuration)
    {
        var services = new ServiceCollection();
        WorkerConfiguration.ConfigureServices(services, configuration);

        using var serviceProvider = services.BuildServiceProvider();
        WorkerConfiguration.EnsureValidatedOptions(serviceProvider);
        await InitializeApplicationAsync(serviceProvider);

        var rootCommand = CommandHandler.BuildRootCommand(serviceProvider);
        await rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task RunWorkerAsync(IConfiguration configuration)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        WorkerConfiguration.ConfigureServices(builder.Services, builder.Configuration);
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<WeeklyPipelineService>());

        using var host = builder.Build();
        WorkerConfiguration.EnsureValidatedOptions(host.Services);
        await InitializeApplicationAsync(host.Services);
        await host.RunAsync();
    }

    private static async Task RunDebugWorkerAsync(
        string[] args,
        string environmentName,
        IConfiguration configuration)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            EnvironmentName = environmentName,
            ContentRootPath = Directory.GetCurrentDirectory()
        });

        builder.Configuration.AddConfiguration(configuration);

        if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        {
            builder.WebHost.UseUrls("http://127.0.0.1:5101");
        }

        WorkerConfiguration.ConfigureServices(builder.Services, builder.Configuration);
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<WeeklyPipelineService>());

        var app = builder.Build();
        WorkerConfiguration.EnsureValidatedOptions(app.Services);
        await InitializeApplicationAsync(app.Services);

        app.MapPost("/debug/pipeline/run-now", async (PipelineRunRequest? request, WeeklyPipelineService pipeline) =>
        {
            var result = await pipeline.TryStartPipelineAsync("admin-debug", request?.ParentId, CancellationToken.None);
            return result switch
            {
                PipelineStartStatus.Started => Results.Accepted(value: new
                {
                    status = "started",
                    message = request?.ParentId is int parentId
                        ? $"Pipeline run started for parent {parentId}."
                        : "Pipeline run started for all active parents."
                }),
                PipelineStartStatus.AlreadyRunning => Results.Conflict(new
                {
                    status = "already-running",
                    message = "A pipeline run is already in progress."
                }),
                PipelineStartStatus.ParentNotFound => Results.NotFound(new
                {
                    status = "parent-not-found",
                    message = request?.ParentId is int parentId
                        ? $"Active parent {parentId} was not found."
                        : "No active parent was found."
                }),
                _ => Results.Problem("Unexpected pipeline run status.")
            };
        });

        await app.RunAsync();
    }

    private static async Task InitializeApplicationAsync(IServiceProvider services)
    {
        const int maxAttempts = 10;
        const int delayMs = 3000;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync();

                var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
                if (pending.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Schema verification failed: {pending.Count} migration(s) still pending after apply: {string.Join(", ", pending)}");
                }

                break;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                Log.Warning(ex, "Database migration attempt {Attempt}/{Max} failed, retrying in {Delay}ms",
                    attempt, maxAttempts, delayMs);
                await Task.Delay(delayMs);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Database migration failed after {MaxAttempts} attempts. Aborting startup.", maxAttempts);
                throw;
            }
        }

        var logger = services.GetRequiredService<ILogger<Program>>();

        using var validationScope = services.CreateScope();
        var validator = validationScope.ServiceProvider.GetRequiredService<StartupValidator>();
        var validationResults = await validator.RunAllChecksAsync();

        foreach (var result in validationResults)
        {
            var level = result.Status switch
            {
                CheckStatus.Pass => LogLevel.Information,
                CheckStatus.Warn => LogLevel.Warning,
                _ => LogLevel.Error
            };

            logger.Log(level, "[{Status}] {Name}: {Detail}", result.Status, result.Name, result.Detail);
        }

        var failCount = validationResults.Count(r => r.Status == CheckStatus.Fail);
        if (failCount > 0)
        {
            logger.LogCritical("{FailCount} startup check(s) failed. Aborting.", failCount);
            Environment.Exit(1);
        }
    }
}
