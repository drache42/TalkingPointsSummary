using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Pipeline;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

/// <summary>
/// Guards the composition root in <see cref="WorkerConfiguration.ConfigureServices"/>. These tests
/// resolve services the pipeline depends on, with scope validation enabled, so a missing
/// registration or a captive scoped dependency fails here rather than at the first pipeline run.
/// </summary>
public class ServiceRegistrationTests
{
    [Fact]
    public void ConfigureServices_ResolvesEventExtractor()
    {
        using var provider = BuildServiceProvider();
        using var scope = provider.CreateScope();

        var extractor = scope.ServiceProvider.GetService<IEventExtractor>();

        extractor.Should().BeOfType<EventExtractor>();
    }

    [Fact]
    public void ConfigureServices_EventExtractorIsScoped_NotSharedAcrossScopes()
    {
        // EventExtractor captures the scoped AppDbContext, so it must not outlive its scope.
        using var provider = BuildServiceProvider();

        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<IEventExtractor>();
        var second = secondScope.ServiceProvider.GetRequiredService<IEventExtractor>();

        first.Should().NotBeSameAs(second);
        firstScope.ServiceProvider.GetRequiredService<IEventExtractor>().Should().BeSameAs(first);
    }

    [Fact]
    public void ConfigureServices_ResolvesSummaryOutputValidatorAsSingleton()
    {
        using var provider = BuildServiceProvider();

        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider.GetService<SummaryOutputValidator>();
        var second = secondScope.ServiceProvider.GetService<SummaryOutputValidator>();

        first.Should().NotBeNull();
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void ConfigureServices_ResolvesSummaryCritic()
    {
        using var provider = BuildServiceProvider();
        using var scope = provider.CreateScope();

        var critic = scope.ServiceProvider.GetService<ISummaryCritic>();

        critic.Should().BeOfType<SummaryCritic>();
    }

    /// <summary>
    /// The orchestrator is where the extractor, the validator, and the critic are actually put to
    /// work. Resolving it proves every one of those registrations exists and fits, which is the
    /// difference between a service being registered and a service being used.
    /// </summary>
    [Fact]
    public void ConfigureServices_ResolvesPipelineOrchestratorWithItsReviewDependencies()
    {
        using var provider = BuildServiceProvider();
        using var scope = provider.CreateScope();

        var orchestrator = scope.ServiceProvider.GetService<PipelineOrchestrator>();

        orchestrator.Should().NotBeNull();
    }

    /// <summary>
    /// Builds the worker's real service provider from a configuration set that validates cleanly.
    /// Scope validation is on so a captive scoped dependency surfaces as a resolution failure.
    /// </summary>
    private static ServiceProvider BuildServiceProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
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
            })
            .Build();

        var services = new ServiceCollection();
        WorkerConfiguration.ConfigureServices(services, configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
