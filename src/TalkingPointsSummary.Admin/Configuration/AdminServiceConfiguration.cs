using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Admin.Services;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Admin.Configuration;

/// <summary>
/// Registers services and feature flags used by the admin application.
/// </summary>
public static class AdminServiceConfiguration
{
    private const string TalkingPointsConnectionName = "TalkingPoints";

    /// <summary>
    /// Registers database access, CRUD services, and the debug pipeline client.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configuration">Application configuration source.</param>
    public static void ConfigureApplicationServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddOptions<DebugFeaturesOptions>()
            .Bind(configuration.GetSection(DebugFeaturesOptions.SectionName));

        var connectionString = configuration.GetConnectionString(TalkingPointsConnectionName)
            ?? throw new InvalidOperationException(
                "Missing required connection string 'TalkingPoints'. Configure ConnectionStrings:TalkingPoints via appsettings, user secrets, or environment variables.");

        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        services.AddParentChildServices();

        services.AddHttpClient<PipelineDebugClient>(client =>
        {
            var workerDebugBaseUrl = configuration["WorkerDebugBaseUrl"];
            if (Uri.TryCreate(workerDebugBaseUrl, UriKind.Absolute, out var baseAddress))
            {
                client.BaseAddress = baseAddress;
            }
        });
    }

    /// <summary>
    /// Returns whether admin debug features are enabled.
    /// </summary>
    /// <param name="configuration">Application configuration source.</param>
    public static bool AreDebugFeaturesEnabled(IConfiguration configuration)
        => DebugFeaturesOptions.IsEnabled(configuration);

    /// <summary>
    /// Returns whether a route should be blocked because debug features are disabled.
    /// </summary>
    /// <param name="path">Request path being evaluated.</param>
    /// <param name="debugFeaturesEnabled">Whether debug features are enabled.</param>
    public static bool ShouldBlockDebugRoute(PathString path, bool debugFeaturesEnabled)
        => !debugFeaturesEnabled && path.StartsWithSegments("/debug");
}