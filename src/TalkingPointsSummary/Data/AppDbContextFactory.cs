using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Hosting;
using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Data;

/// <summary>
/// Allows EF Core tooling (dotnet ef migrations add) to build the DbContext
/// without executing Program.cs or requiring a running database.
/// The design-time factory uses the same configuration sources as runtime and
/// requires ConnectionStrings:TalkingPoints to be configured.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environments.Development;

        var configuration = WorkerConfiguration.BuildConfiguration(Directory.GetCurrentDirectory(), environmentName);
        var connectionString = WorkerConfiguration.GetRequiredConnectionString(configuration);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString,
                npgsql => npgsql.MigrationsAssembly("TalkingPointsSummary"))
            .Options;

        return new AppDbContext(options);
    }
}
