using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Admin.Services;
using TalkingPointsSummary.Data;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Admin.Configuration;

public static class AdminServiceConfiguration
{
    private const string TalkingPointsConnectionName = "TalkingPoints";

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

    public static bool AreDebugFeaturesEnabled(IConfiguration configuration)
        => DebugFeaturesOptions.IsEnabled(configuration);

    public static bool ShouldBlockDebugRoute(PathString path, bool debugFeaturesEnabled)
        => !debugFeaturesEnabled && path.StartsWithSegments("/debug");
}