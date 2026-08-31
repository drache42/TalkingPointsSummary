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

    [Fact]
    public void EnsureValidatedOptions_UnknownThinkingMode_Throws()
    {
        using var provider = BuildServiceProvider(BaseConfig(new Dictionary<string, string?>
        {
            ["Ai:Profiles:Summarization:Thinking"] = "extended",
        }));

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Ai:Profiles:Summarization:Thinking*");
    }

    [Fact]
    public void EnsureValidatedOptions_UnknownEffortLevel_Throws()
    {
        using var provider = BuildServiceProvider(BaseConfig(new Dictionary<string, string?>
        {
            ["Ai:Profiles:Critique:Effort"] = "extreme",
        }));

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Ai:Profiles:Critique:Effort*");
    }

    [Fact]
    public void EnsureValidatedOptions_BudgetThinkingBelowMinimumBudget_Throws()
    {
        using var provider = BuildServiceProvider(BaseConfig(new Dictionary<string, string?>
        {
            ["Ai:Profiles:Categorization:Thinking"] = "budget",
            ["Ai:Profiles:Categorization:ThinkingBudgetTokens"] = "512",
            ["Ai:Profiles:Categorization:MaxTokens"] = "4096",
        }));

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Ai:Profiles:Categorization:ThinkingBudgetTokens*");
    }

    [Fact]
    public void EnsureValidatedOptions_BudgetThinkingBudgetNotLessThanMaxTokens_Throws()
    {
        using var provider = BuildServiceProvider(BaseConfig(new Dictionary<string, string?>
        {
            ["Ai:Profiles:Categorization:Thinking"] = "budget",
            ["Ai:Profiles:Categorization:ThinkingBudgetTokens"] = "4096",
            ["Ai:Profiles:Categorization:MaxTokens"] = "4096",
        }));

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Ai:Profiles:Categorization:ThinkingBudgetTokens*");
    }

    [Fact]
    public void EnsureValidatedOptions_UndersizedThinkingBudgetIgnoredWhenThinkingIsNone_Passes()
    {
        using var provider = BuildServiceProvider(BaseConfig(new Dictionary<string, string?>
        {
            ["Ai:Profiles:Categorization:Thinking"] = "none",
            ["Ai:Profiles:Categorization:ThinkingBudgetTokens"] = "1",
        }));

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureValidatedOptions_MaxTokensBelowOne_Throws()
    {
        using var provider = BuildServiceProvider(BaseConfig(new Dictionary<string, string?>
        {
            ["Ai:Profiles:Critique:MaxTokens"] = "0",
        }));

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Ai:Profiles:Critique:MaxTokens*");
    }

    [Fact]
    public void EnsureValidatedOptions_MissingCritiqueModelId_Throws()
    {
        using var provider = BuildServiceProvider(BaseConfig(new Dictionary<string, string?>
        {
            ["Ai:Profiles:Critique:ModelId"] = "",
        }));

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Ai:Profiles:Critique:ModelId*");
    }

    [Fact]
    public void EnsureValidatedOptions_ValidReasoningSettings_BindsProfiles()
    {
        using var provider = BuildServiceProvider(BaseConfig(new Dictionary<string, string?>
        {
            ["Ai:Profiles:Categorization:Thinking"] = "budget",
            ["Ai:Profiles:Categorization:ThinkingBudgetTokens"] = "2048",
            ["Ai:Profiles:Categorization:MaxTokens"] = "8192",
            ["Ai:Profiles:Critique:Effort"] = "xhigh",
        }));

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);
        act.Should().NotThrow();

        var profiles = provider.GetRequiredService<IOptions<AiOptions>>().Value.Profiles;

        profiles.Categorization.Thinking.Should().Be("budget");
        profiles.Categorization.ThinkingBudgetTokens.Should().Be(2048);
        profiles.Categorization.MaxTokens.Should().Be(8192);
        profiles.Categorization.Effort.Should().BeNull();
        profiles.Critique.Effort.Should().Be("xhigh");
    }

    [Fact]
    public void EnsureValidatedOptions_ProfileDefaults_AreValidAndUseAdaptiveThinkingForClaude5()
    {
        using var provider = BuildServiceProvider(BaseConfig([]));

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);
        act.Should().NotThrow();

        var profiles = provider.GetRequiredService<IOptions<AiOptions>>().Value.Profiles;

        profiles.Summarization.ModelId.Should().Be("claude-sonnet-5");
        profiles.Summarization.MaxTokens.Should().Be(32000);
        profiles.Summarization.Thinking.Should().Be("adaptive");
        profiles.Summarization.Effort.Should().Be("high");

        profiles.Critique.ModelId.Should().Be("claude-sonnet-5");
        profiles.Critique.MaxTokens.Should().Be(8192);
        profiles.Critique.Thinking.Should().Be("adaptive");
        profiles.Critique.Effort.Should().Be("high");

        // Haiku 4.5 rejects adaptive thinking, so the Haiku profiles must not request it.
        profiles.Categorization.Thinking.Should().Be("none");
        profiles.Categorization.Effort.Should().BeNull();
        profiles.Validation.ModelId.Should().Be("claude-haiku-4-5-20251001");
        profiles.Validation.Thinking.Should().Be("none");
        profiles.Validation.Effort.Should().BeNull();
    }

    /// <summary>
    /// Returns a configuration set that validates cleanly, with <paramref name="overrides"/>
    /// applied on top so a single rule can be exercised in isolation.
    /// </summary>
    private static Dictionary<string, string?> BaseConfig(Dictionary<string, string?> overrides)
    {
        var values = new Dictionary<string, string?>
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
        };

        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return values;
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