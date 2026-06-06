using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Tests;

public class ConfigurationOptionsValidationTests
{
    [Fact]
    public void EnsureValidatedOptions_MissingAnthropicApiKey_Throws()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TalkingPoints"] = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
            ["Ai:Provider"] = "Anthropic",
            // Ai:Anthropic:ApiKey intentionally omitted
            ["Browserless:BaseUrl"] = "http://localhost:3000",
            ["NewsletterScrapingSecurity:Enabled"] = "true",
            ["TalkingPointsApi:MaxPagesPerRun"] = "3",
            ["Smtp:Host"] = "localhost",
            ["Smtp:Port"] = "1025",
            ["Smtp:FromEmail"] = "dev@example.com",
            ["PipelineSchedule:DayOfWeek"] = "1",
            ["PipelineSchedule:Hour"] = "8",
        });

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Ai:Anthropic:ApiKey*");
    }

    [Fact]
    public void EnsureValidatedOptions_InvalidBrowserlessUrl_Throws()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TalkingPoints"] = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
            ["Ai:Provider"] = "Anthropic",
            ["Ai:Anthropic:ApiKey"] = "test-key",
            ["Browserless:BaseUrl"] = "not-a-url",
            ["NewsletterScrapingSecurity:Enabled"] = "true",
            ["TalkingPointsApi:MaxPagesPerRun"] = "3",
            ["Smtp:Host"] = "localhost",
            ["Smtp:Port"] = "1025",
            ["Smtp:FromEmail"] = "dev@example.com",
            ["PipelineSchedule:DayOfWeek"] = "1",
            ["PipelineSchedule:Hour"] = "8",
        });

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Browserless:BaseUrl*");
    }

    [Fact]
    public void EnsureValidatedOptions_InvalidSchedule_Throws()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TalkingPoints"] = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
            ["Ai:Provider"] = "Anthropic",
            ["Ai:Anthropic:ApiKey"] = "test-key",
            ["Browserless:BaseUrl"] = "http://localhost:3000",
            ["NewsletterScrapingSecurity:Enabled"] = "true",
            ["TalkingPointsApi:MaxPagesPerRun"] = "3",
            ["Smtp:Host"] = "localhost",
            ["Smtp:Port"] = "1025",
            ["Smtp:FromEmail"] = "dev@example.com",
            ["PipelineSchedule:DayOfWeek"] = "7",
            ["PipelineSchedule:Hour"] = "24",
        });

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void EnsureValidatedOptions_SmtpCredentialsBothEmpty_Passes()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TalkingPoints"] = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
            ["Ai:Provider"] = "Anthropic",
            ["Ai:Anthropic:ApiKey"] = "test-key",
            ["Browserless:BaseUrl"] = "http://localhost:3000",
            ["NewsletterScrapingSecurity:Enabled"] = "true",
            ["TalkingPointsApi:MaxPagesPerRun"] = "3",
            ["Smtp:Host"] = "localhost",
            ["Smtp:Port"] = "1025",
            ["Smtp:Username"] = "",
            ["Smtp:Password"] = "",
            ["Smtp:FromEmail"] = "dev@example.com",
            ["PipelineSchedule:DayOfWeek"] = "1",
            ["PipelineSchedule:Hour"] = "8",
        });

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().NotThrow();
        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void EnsureValidatedOptions_SmtpCredentialPairMismatch_Throws()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TalkingPoints"] = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
            ["Ai:Provider"] = "Anthropic",
            ["Ai:Anthropic:ApiKey"] = "test-key",
            ["Browserless:BaseUrl"] = "http://localhost:3000",
            ["NewsletterScrapingSecurity:Enabled"] = "true",
            ["TalkingPointsApi:MaxPagesPerRun"] = "3",
            ["Smtp:Host"] = "localhost",
            ["Smtp:Port"] = "1025",
            ["Smtp:Username"] = "user-only",
            ["Smtp:FromEmail"] = "dev@example.com",
            ["PipelineSchedule:DayOfWeek"] = "1",
            ["PipelineSchedule:Hour"] = "8",
        });

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Smtp:Username and Smtp:Password*");
    }

    [Fact]
    public void EnsureValidatedOptions_InvalidTimezone_Throws()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TalkingPoints"] = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
            ["Ai:Provider"] = "Anthropic",
            ["Ai:Anthropic:ApiKey"] = "test-key",
            ["Browserless:BaseUrl"] = "http://localhost:3000",
            ["NewsletterScrapingSecurity:Enabled"] = "true",
            ["TalkingPointsApi:MaxPagesPerRun"] = "3",
            ["Smtp:Host"] = "localhost",
            ["Smtp:Port"] = "1025",
            ["Smtp:FromEmail"] = "dev@example.com",
            ["PipelineSchedule:DayOfWeek"] = "1",
            ["PipelineSchedule:Hour"] = "8",
            ["PipelineSchedule:TimeZone"] = "Not/ATimeZone",
        });

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*PipelineSchedule:TimeZone*");
    }

    private static ServiceProvider BuildServiceProvider(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        WorkerConfiguration.ConfigureServices(services, configuration);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Simulates what <see cref="WorkerConfiguration.BuildConfiguration"/> does at runtime:
    /// runs the migration pass and builds the final config from the original values plus
    /// any promoted legacy keys.
    /// </summary>
    private static ServiceProvider BuildServiceProviderWithMigration(Dictionary<string, string?> values)
    {
        var intermediate = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var (promoted, _) = ConfigMigrationRunner.Run(intermediate, ConfigKeyMigrations.All);

        var configBuilder = new ConfigurationBuilder().AddInMemoryCollection(values);
        if (promoted.Count > 0)
            configBuilder.AddInMemoryCollection(promoted);

        var services = new ServiceCollection();
        WorkerConfiguration.ConfigureServices(services, configBuilder.Build());
        return services.BuildServiceProvider();
    }

    // ── End-to-end migration pipeline test ───────────────────────────────────

    [Fact]
    public void EnsureValidatedOptions_LegacyAnthropicApiKey_MigratesAndBindsSuccessfully()
    {
        using var provider = BuildServiceProviderWithMigration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TalkingPoints"] = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
            ["Anthropic:ApiKey"] = "sk-migrated",   // old key — no Ai:* keys present
            ["Browserless:BaseUrl"] = "http://localhost:3000",
            ["NewsletterScrapingSecurity:Enabled"] = "true",
            ["TalkingPointsApi:MaxPagesPerRun"] = "3",
            ["Smtp:Host"] = "localhost",
            ["Smtp:Port"] = "1025",
            ["Smtp:FromEmail"] = "dev@example.com",
            ["PipelineSchedule:DayOfWeek"] = "1",
            ["PipelineSchedule:Hour"] = "8",
        });

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);
        act.Should().NotThrow();

        var aiOptions = provider.GetRequiredService<IOptions<AiOptions>>().Value;
        aiOptions.Anthropic.ApiKey.Should().Be("sk-migrated");
        aiOptions.Provider.Should().Be("Anthropic");
    }
}