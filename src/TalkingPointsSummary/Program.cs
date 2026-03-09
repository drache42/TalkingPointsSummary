using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Serilog;
using TalkingPointsSummary.Commands;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

// --- Build IConfiguration (appsettings.json + environment variables) ---
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{environment}.json", optional: true)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

// --- Configure Serilog from appsettings ---
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

try
{

// --- Build configuration from environment variables ---
var appSettings = new AppSettings
{
    ConnectionString = configuration["CONNECTION_STRING"]
        ?? "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
    AnthropicApiKey = configuration["ANTHROPIC_API_KEY"] ?? string.Empty,
    BrowserlessUrl = configuration["BROWSERLESS_URL"] ?? "http://browserless:3000",
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

// --- Build DI container (shared between CLI and Worker modes) ---
var services = new ServiceCollection();

// Configuration
services.AddSingleton(Options.Create(appSettings));
services.AddLogging(builder => builder.ClearProviders().AddSerilog(Log.Logger));

// Database
services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(appSettings.ConnectionString));

// HTTP clients
services.AddHttpClient<TalkingPointsApiClient>();
services.AddHttpClient<MessageCategorizer>();
services.AddHttpClient<NewsletterScraper>();
services.AddHttpClient<SummaryGenerator>();

// Services
services.AddScoped<MessageDeduplicator>();
services.AddSingleton<MarkdownConverter>();
services.AddScoped<EmailSender>();
services.AddScoped<PipelineOrchestrator>();
services.AddSingleton<WeeklyPipelineService>();
services.AddScoped<StartupValidator>();

var serviceProvider = services.BuildServiceProvider();

// --- Apply database migrations on startup ---
using (var scope = serviceProvider.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

// --- Route: CLI mode or Worker mode ---
if (args.Length > 0)
{
    // CLI mode: parse commands and exit
    var rootCommand = CommandHandler.BuildRootCommand(serviceProvider);
    await rootCommand.Parse(args).InvokeAsync();
}
else
{
    // Worker mode: run as long-lived background service
    logger.LogInformation("Starting Talking Points Summary worker service");

    using (var validationScope = serviceProvider.CreateScope())
    {
        var validator = validationScope.ServiceProvider.GetRequiredService<StartupValidator>();
        var results = await validator.RunAllChecksAsync();

        foreach (var result in results)
        {
            var level = result.Status switch
            {
                CheckStatus.Pass => Microsoft.Extensions.Logging.LogLevel.Information,
                CheckStatus.Warn => Microsoft.Extensions.Logging.LogLevel.Warning,
                _ => Microsoft.Extensions.Logging.LogLevel.Error
            };
            logger.Log(level, "[{Status}] {Name}: {Detail}", result.Status, result.Name, result.Detail);
        }

        var failCount = results.Count(r => r.Status == CheckStatus.Fail);
        if (failCount > 0)
        {
            logger.LogCritical("{FailCount} startup check(s) failed. Aborting worker.", failCount);
            Environment.Exit(1);
        }
    }

    var pipeline = serviceProvider.GetRequiredService<WeeklyPipelineService>();
    await pipeline.StartAsync(CancellationToken.None);

    // Keep alive until Ctrl+C / SIGTERM
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException) { }

    await pipeline.StopAsync(CancellationToken.None);
    logger.LogInformation("Worker service stopped");
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
