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
            ["Browserless:BaseUrl"] = "http://localhost:3000",
            ["Smtp:Host"] = "localhost",
            ["Smtp:Port"] = "1025",
            ["Smtp:FromEmail"] = "dev@example.com",
            ["PipelineSchedule:DayOfWeek"] = "1",
            ["PipelineSchedule:Hour"] = "8",
        });

        var act = () => WorkerConfiguration.EnsureValidatedOptions(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Anthropic:ApiKey*");
    }

    [Fact]
    public void EnsureValidatedOptions_InvalidBrowserlessUrl_Throws()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TalkingPoints"] = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
            ["Anthropic:ApiKey"] = "test-key",
            ["Browserless:BaseUrl"] = "not-a-url",
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
            ["Anthropic:ApiKey"] = "test-key",
            ["Browserless:BaseUrl"] = "http://localhost:3000",
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
            ["Anthropic:ApiKey"] = "test-key",
            ["Browserless:BaseUrl"] = "http://localhost:3000",
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
    }

    [Fact]
    public void EnsureValidatedOptions_SmtpCredentialPairMismatch_Throws()
    {
        using var provider = BuildServiceProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TalkingPoints"] = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
            ["Anthropic:ApiKey"] = "test-key",
            ["Browserless:BaseUrl"] = "http://localhost:3000",
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

    private static ServiceProvider BuildServiceProvider(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        WorkerConfiguration.ConfigureServices(services, configuration);

        return services.BuildServiceProvider();
    }
}