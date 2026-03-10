using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Configuration;

internal static class WorkerConfiguration
{
    private const string TalkingPointsConnectionName = "TalkingPoints";

    public static IConfigurationRoot BuildConfiguration(string basePath, string environmentName)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true);

        if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            builder.AddUserSecrets<Program>(optional: true);
        }

        builder.AddEnvironmentVariables();

        return builder.Build();
    }

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        AddValidatedOptions(services, configuration);

        services.AddLogging(builder => builder.ClearProviders().AddSerilog(Log.Logger));

        var connectionString = GetRequiredConnectionString(configuration);

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(Program).Assembly.GetName().Name ?? "TalkingPointsSummary")));

        services.AddHttpClient<ITalkingPointsApiClient, TalkingPointsApiClient>();
        services.AddHttpClient<IMessageCategorizer, MessageCategorizer>();
        services.AddHttpClient<INewsletterScraper, NewsletterScraper>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });
        services.AddHttpClient<ISummaryGenerator, SummaryGenerator>();

        services.AddScoped<IMessageDeduplicator, MessageDeduplicator>();
        services.AddSingleton<IMarkdownConverter, MarkdownConverter>();
        services.AddScoped<IEmailSender, EmailSender>();
        services.AddScoped<PipelineOrchestrator>();
        services.AddSingleton<WeeklyPipelineService>();
        services.AddScoped<StartupValidator>();
    }

    public static string GetRequiredConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(TalkingPointsConnectionName);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        throw new InvalidOperationException(
            $"Missing required connection string '{TalkingPointsConnectionName}'. Configure ConnectionStrings:{TalkingPointsConnectionName} via appsettings, user secrets, or environment variables.");
    }

    public static void EnsureValidatedOptions(IServiceProvider services)
    {
        _ = services.GetRequiredService<IOptions<AnthropicOptions>>().Value;
        _ = services.GetRequiredService<IOptions<BrowserlessOptions>>().Value;
        _ = services.GetRequiredService<IOptions<SmtpOptions>>().Value;
        _ = services.GetRequiredService<IOptions<PipelineScheduleOptions>>().Value;
    }

    private static void AddValidatedOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AnthropicOptions>()
            .Bind(configuration.GetSection(AnthropicOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey), "Anthropic:ApiKey is required.")
            .ValidateOnStart();

        services.AddOptions<BrowserlessOptions>()
            .Bind(configuration.GetSection(BrowserlessOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _), "Browserless:BaseUrl must be a valid absolute URL.")
            .ValidateOnStart();

        services.AddOptions<SmtpOptions>()
            .Bind(configuration.GetSection(SmtpOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options =>
                {
                    var hasUsername = !string.IsNullOrWhiteSpace(options.Username);
                    var hasPassword = !string.IsNullOrWhiteSpace(options.Password);
                    return hasUsername == hasPassword;
                },
                "Smtp:Username and Smtp:Password must either both be provided or both be empty.")
            .ValidateOnStart();

        services.AddOptions<PipelineScheduleOptions>()
            .Bind(configuration.GetSection(PipelineScheduleOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}