using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TalkingPointsSummary.Admin.Configuration;
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
    }
}