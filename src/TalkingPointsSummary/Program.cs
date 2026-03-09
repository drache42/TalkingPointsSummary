using System.CommandLine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using TalkingPointsSummary.Commands;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary;

internal sealed class Program
{
    public static async Task Main(string[] args)
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environments.Production;

        var configuration = BuildConfiguration(environmentName);

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        try
        {
            var appSettings = BuildAppSettings(configuration);

            if (args.Length > 0)
            {
                await RunCliAsync(args, appSettings);
            }
            else if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
            {
                await RunDevelopmentWorkerAsync(args, environmentName, configuration, appSettings);
            }
            else
            {
                await RunWorkerAsync(configuration, appSettings);
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
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private static AppSettings BuildAppSettings(IConfiguration configuration)
    {
        var browserlessUrl = configuration["BROWSERLESS_URL"];
        if (string.IsNullOrWhiteSpace(browserlessUrl))
        {
            var browserlessHost = configuration["BROWSERLESS_HOST"];
            var browserlessPort = configuration["BROWSERLESS_PORT"];

            if (!string.IsNullOrWhiteSpace(browserlessHost) && !string.IsNullOrWhiteSpace(browserlessPort))
            {
                browserlessUrl = $"http://{browserlessHost}:{browserlessPort}";
            }
        }

        return new AppSettings
        {
            ConnectionString = configuration["CONNECTION_STRING"]
                ?? "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
            AnthropicApiKey = configuration["ANTHROPIC_API_KEY"] ?? string.Empty,
            BrowserlessUrl = browserlessUrl ?? "http://browserless:3000",
            Smtp = new SmtpSettings
            {
                Host = configuration["SMTP_HOST"] ?? "smtp.gmail.com",
                Port = int.TryParse(configuration["SMTP_PORT"], out var port) ? port : 587,
                Username = configuration["SMTP_USERNAME"] ?? string.Empty,
                Password = configuration["SMTP_PASSWORD"] ?? string.Empty,
                FromEmail = configuration["SMTP_FROM"] ?? string.Empty,
            },
            ScheduleDayOfWeek = int.TryParse(configuration["SCHEDULE_DAY"], out var day) ? day : 1,
            ScheduleHour = int.TryParse(configuration["SCHEDULE_HOUR"], out var hour) ? hour : 8,
        };
    }

    private static void ConfigureServices(IServiceCollection services, AppSettings appSettings)
    {
        services.AddSingleton(Options.Create(appSettings));
        services.AddLogging(builder => builder.ClearProviders().AddSerilog(Log.Logger));

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(appSettings.ConnectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(Program).Assembly.GetName().Name ?? "TalkingPointsSummary")));

        services.AddHttpClient<TalkingPointsApiClient>();
        services.AddHttpClient<MessageCategorizer>();
        services.AddHttpClient<NewsletterScraper>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });
        services.AddHttpClient<SummaryGenerator>();

        services.AddScoped<MessageDeduplicator>();
        services.AddSingleton<MarkdownConverter>();
        services.AddScoped<EmailSender>();
        services.AddScoped<PipelineOrchestrator>();
        services.AddSingleton<WeeklyPipelineService>();
        services.AddScoped<StartupValidator>();
    }

    private static async Task RunCliAsync(string[] args, AppSettings appSettings)
    {
        var services = new ServiceCollection();
        ConfigureServices(services, appSettings);

        using var serviceProvider = services.BuildServiceProvider();
        await InitializeApplicationAsync(serviceProvider);

        var rootCommand = CommandHandler.BuildRootCommand(serviceProvider);
        await rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task RunWorkerAsync(IConfiguration configuration, AppSettings appSettings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        ConfigureServices(builder.Services, appSettings);
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<WeeklyPipelineService>());

        using var host = builder.Build();
        await InitializeApplicationAsync(host.Services);
        await host.RunAsync();
    }

    private static async Task RunDevelopmentWorkerAsync(
        string[] args,
        string environmentName,
        IConfiguration configuration,
        AppSettings appSettings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            EnvironmentName = environmentName,
            ContentRootPath = Directory.GetCurrentDirectory()
        });

        builder.Configuration.AddConfiguration(configuration);

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        {
            builder.WebHost.UseUrls("http://127.0.0.1:5101");
        }

        ConfigureServices(builder.Services, appSettings);
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<WeeklyPipelineService>());

        var app = builder.Build();
        await InitializeApplicationAsync(app.Services);

        app.MapPost("/debug/pipeline/run-now", async (WeeklyPipelineService pipeline) =>
        {
            var result = await pipeline.TryRunFullPipelineAsync("admin-debug", CancellationToken.None);
            return result switch
            {
                PipelineRunStatus.Completed => Results.Ok(new
                {
                    status = "completed",
                    message = "Pipeline run complete."
                }),
                PipelineRunStatus.AlreadyRunning => Results.Conflict(new
                {
                    status = "already-running",
                    message = "A pipeline run is already in progress."
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
