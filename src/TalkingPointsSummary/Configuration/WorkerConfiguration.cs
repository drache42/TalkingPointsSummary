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
        // Stateless: holds only compiled regexes and lookup tables, and takes the current date
        // as a Validate argument rather than reading a clock.
        services.AddSingleton<SummaryOutputValidator>();
        services.AddScoped<IEmailSender, EmailSender>();
        services.AddScoped<IMessageCategorizer, MessageCategorizer>();
        services.AddScoped<IEventExtractor, EventExtractor>();
        services.AddScoped<ISummaryGenerator, SummaryGenerator>();
        // Scoped rather than singleton: it captures the typed IAiClient, which AddHttpClient
        // registers as transient so its handler can be rotated.
        services.AddScoped<ISummaryCritic, SummaryCritic>();
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

    private const int MinimumThinkingBudgetTokens = 1024;

    private static bool HasValidThinkingMode(AiProfileOptions profile) =>
        AiThinkingModes.All.Contains(profile.Thinking, StringComparer.OrdinalIgnoreCase);

    private static bool HasValidEffort(AiProfileOptions profile) =>
        profile.Effort is null || AiEffortLevels.All.Contains(profile.Effort, StringComparer.OrdinalIgnoreCase);

    private static bool HasValidMaxTokens(AiProfileOptions profile) => profile.MaxTokens >= 1;

    private static bool HasValidThinkingBudget(AiProfileOptions profile)
    {
        if (!string.Equals(profile.Thinking, AiThinkingModes.Budget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return profile.ThinkingBudgetTokens >= MinimumThinkingBudgetTokens
            && profile.ThinkingBudgetTokens < profile.MaxTokens;
    }

    private static string ThinkingModeMessage(string profileName) =>
        $"Ai:Profiles:{profileName}:Thinking must be one of: {string.Join(", ", AiThinkingModes.All)}.";

    private static string EffortMessage(string profileName) =>
        $"Ai:Profiles:{profileName}:Effort must be one of: {string.Join(", ", AiEffortLevels.All)}, or omitted.";

    private static string MaxTokensMessage(string profileName) =>
        $"Ai:Profiles:{profileName}:MaxTokens must be at least 1.";

    private static string ThinkingBudgetMessage(string profileName) =>
        $"Ai:Profiles:{profileName}:ThinkingBudgetTokens must be at least {MinimumThinkingBudgetTokens} and less than Ai:Profiles:{profileName}:MaxTokens when Thinking is 'budget'.";

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
            .Validate(options => !string.IsNullOrWhiteSpace(options.Profiles.Categorization.ModelId), "Ai:Profiles:Categorization:ModelId is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Profiles.Summarization.ModelId), "Ai:Profiles:Summarization:ModelId is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Profiles.Critique.ModelId), "Ai:Profiles:Critique:ModelId is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Profiles.Validation.ModelId), "Ai:Profiles:Validation:ModelId is required.")
            .Validate(options => HasValidThinkingMode(options.Profiles.Categorization), ThinkingModeMessage("Categorization"))
            .Validate(options => HasValidThinkingMode(options.Profiles.Summarization), ThinkingModeMessage("Summarization"))
            .Validate(options => HasValidThinkingMode(options.Profiles.Critique), ThinkingModeMessage("Critique"))
            .Validate(options => HasValidThinkingMode(options.Profiles.Validation), ThinkingModeMessage("Validation"))
            .Validate(options => HasValidEffort(options.Profiles.Categorization), EffortMessage("Categorization"))
            .Validate(options => HasValidEffort(options.Profiles.Summarization), EffortMessage("Summarization"))
            .Validate(options => HasValidEffort(options.Profiles.Critique), EffortMessage("Critique"))
            .Validate(options => HasValidEffort(options.Profiles.Validation), EffortMessage("Validation"))
            .Validate(options => HasValidMaxTokens(options.Profiles.Categorization), MaxTokensMessage("Categorization"))
            .Validate(options => HasValidMaxTokens(options.Profiles.Summarization), MaxTokensMessage("Summarization"))
            .Validate(options => HasValidMaxTokens(options.Profiles.Critique), MaxTokensMessage("Critique"))
            .Validate(options => HasValidMaxTokens(options.Profiles.Validation), MaxTokensMessage("Validation"))
            .Validate(options => HasValidThinkingBudget(options.Profiles.Categorization), ThinkingBudgetMessage("Categorization"))
            .Validate(options => HasValidThinkingBudget(options.Profiles.Summarization), ThinkingBudgetMessage("Summarization"))
            .Validate(options => HasValidThinkingBudget(options.Profiles.Critique), ThinkingBudgetMessage("Critique"))
            .Validate(options => HasValidThinkingBudget(options.Profiles.Validation), ThinkingBudgetMessage("Validation"))
            .Validate(options => AiModelReasoning.IsCompatible(options.Profiles.Categorization.ModelId, options.Profiles.Categorization.Thinking), AiModelReasoning.IncompatibleMessage("Categorization"))
            .Validate(options => AiModelReasoning.IsCompatible(options.Profiles.Summarization.ModelId, options.Profiles.Summarization.Thinking), AiModelReasoning.IncompatibleMessage("Summarization"))
            .Validate(options => AiModelReasoning.IsCompatible(options.Profiles.Critique.ModelId, options.Profiles.Critique.Thinking), AiModelReasoning.IncompatibleMessage("Critique"))
            .Validate(options => AiModelReasoning.IsCompatible(options.Profiles.Validation.ModelId, options.Profiles.Validation.Thinking), AiModelReasoning.IncompatibleMessage("Validation"))
            .ValidateOnStart();

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