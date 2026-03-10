using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Admin.Configuration;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class AdminServiceConfigurationTests
{
    [Fact]
    public void ConfigureApplicationServices_AllowsScopedCrudServicesToResolve()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TalkingPoints"] = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
                ["WorkerDebugBaseUrl"] = "http://localhost:5101/"
            })
            .Build();

        AdminServiceConfiguration.ConfigureApplicationServices(services, configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();
        var scopedProvider = scope.ServiceProvider;

        scopedProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().Should().NotBeNull();
        scopedProvider.GetRequiredService<AppDbContext>().Should().NotBeNull();
        scopedProvider.GetRequiredService<IParentService>().Should().NotBeNull();
        scopedProvider.GetRequiredService<IChildService>().Should().NotBeNull();
        scopedProvider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
        scopedProvider.GetRequiredService<IOptions<DebugFeaturesOptions>>().Value.Enabled.Should().BeFalse();
    }

    [Fact]
    public void ConfigureApplicationServices_BindsDebugFeaturesOptions()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TalkingPoints"] = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres",
                ["DebugFeatures:Enabled"] = "true"
            })
            .Build();

        AdminServiceConfiguration.ConfigureApplicationServices(services, configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();
        scope.ServiceProvider
            .GetRequiredService<IOptions<DebugFeaturesOptions>>()
            .Value.Enabled
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(false, "/debug", true)]
    [InlineData(false, "/debug/run-now", true)]
    [InlineData(false, "/parents", false)]
    [InlineData(true, "/debug", false)]
    public void ShouldBlockDebugRoute_ReturnsExpectedResult(bool debugFeaturesEnabled, string path, bool expected)
    {
        var shouldBlock = AdminServiceConfiguration.ShouldBlockDebugRoute(new PathString(path), debugFeaturesEnabled);

        shouldBlock.Should().Be(expected);
    }
}