using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Serilog;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Configuration;

internal static class WorkerConfiguration
{
    private const string TalkingPointsConnectionName = "TalkingPoints";
    private static readonly HttpRetryStrategyOptions SharedRetryOptions = new()
    {
        BackoffType = DelayBackoffType.Exponential,
        MaxRetryAttempts = 3,
        UseJitter = true,
        Delay = TimeSpan.FromSeconds(1)
    };

    public static (IConfigurationRoot Config, IReadOnlyList<string> DeprecationWarnings) BuildConfiguration(
        string basePath, string environmentName)
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

        // First pass: detect legacy keys and compute promoted values.
        var intermediate = builder.Build();
        var (promoted, warnings) = ConfigMigrationRunner.Run(intermediate, ConfigKeyMigrations.All);

        if (promoted.Count == 0)
        {
            return (intermediate, []);
        }

        // Second pass: inject promoted values at highest priority and rebuild.
        builder.AddInMemoryCollection(promoted);
        return (builder.Build(), warnings);
    }

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        AddValidatedOptions(services, configuration);

        services.AddLogging(builder => builder.ClearProviders().AddSerilog(Log.Logger));
        services.AddSingleton(TimeProvider.System);

        var connectionString = GetRequiredConnectionString(configuration);

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(Program).Assembly.GetName().Name ?? "TalkingPointsSummary")));

        services.AddHttpClient<ITalkingPointsApiClient, TalkingPointsApiClient>()
            .AddResilienceHandler("talkingpoints-retry", static builder =>
            {
                builder.AddRetry(SharedRetryOptions);
            });

        services.AddHttpClient<IAiClient, AnthropicAiClient>(client =>
            {
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .AddResilienceHandler("anthropic-retry", static builder =>
            {
                builder.AddRetry(SharedRetryOptions);
            });
        services.AddSingleton<IAiReasoningCompatibility, AnthropicModelReasoning>();

        services.AddHttpClient<INewsletterScraper, NewsletterScraper>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(90);
            })
            .AddResilienceHandler("browserless-retry", static builder =>
            {
                builder.AddRetry(SharedRetryOptions);
            });

        services.AddSingleton<IHostAddressResolver, HostAddressResolver>();
        services.AddScoped<INewsletterUrlValidator, NewsletterUrlValidator>();
        services.AddScoped<IMessageDeduplicator, MessageDeduplicator>();
        services.AddSingleton<IMarkdownConverter, MarkdownConverter>();
        services.AddScoped<IEmailSender, EmailSender>();
        services.AddScoped<IMessageCategorizer, MessageCategorizer>();
        services.AddScoped<ISummaryGenerator, SummaryGenerator>();
        services.AddParentChildServices();
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
        _ = services.GetRequiredService<IOptions<AiOptions>>().Value;
        _ = services.GetRequiredService<IOptions<BrowserlessOptions>>().Value;
        _ = services.GetRequiredService<IOptions<DebugFeaturesOptions>>().Value;
        _ = services.GetRequiredService<IOptions<NewsletterScrapingSecurityOptions>>().Value;
        _ = services.GetRequiredService<IOptions<TalkingPointsApiOptions>>().Value;
        _ = services.GetRequiredService<IOptions<SmtpOptions>>().Value;
        _ = services.GetRequiredService<IOptions<PipelineScheduleOptions>>().Value;
    }

    private static void AddValidatedOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => !string.IsNullOrWhiteSpace(options.Provider), "Ai:Provider is required.")
            .Validate(
                options => string.Equals(options.Provider, "Anthropic", StringComparison.OrdinalIgnoreCase),
                "Ai:Provider must be 'Anthropic'. No other providers are supported yet.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Anthropic.ApiKey),
                "Ai:Anthropic:ApiKey is required.")
            .ValidateOnStart();

        // Per-profile field rules (ModelId, Thinking, Effort, MaxTokens, ThinkingBudgetTokens) are
        // checked by looping over the three profiles rather than one .Validate() call per profile
        // per rule. See AiProfileFieldValidator.
        services.AddSingleton<IValidateOptions<AiOptions>, AiProfileFieldValidator>();

        // Model/thinking compatibility depends on the AI provider's own model-family rules, so it
        // is resolved through IAiReasoningCompatibility rather than checked inline here. See
        // AiReasoningCompatibilityValidator.
        services.AddSingleton<IValidateOptions<AiOptions>, AiReasoningCompatibilityValidator>();

        services.AddOptions<BrowserlessOptions>()
            .Bind(configuration.GetSection(BrowserlessOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _), "Browserless:BaseUrl must be a valid absolute URL.")
            .ValidateOnStart();

        services.AddOptions<DebugFeaturesOptions>()
            .Bind(configuration.GetSection(DebugFeaturesOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<NewsletterScrapingSecurityOptions>()
            .Bind(configuration.GetSection(NewsletterScrapingSecurityOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<TalkingPointsApiOptions>()
            .Bind(configuration.GetSection(TalkingPointsApiOptions.SectionName))
            .ValidateDataAnnotations()
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