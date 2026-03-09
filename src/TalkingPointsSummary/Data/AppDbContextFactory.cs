using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TalkingPointsSummary.Data;

/// <summary>
/// Allows EF Core tooling (dotnet ef migrations add) to build the DbContext
/// without executing Program.cs or requiring a running database.
/// The connection string here is used only at design-time; runtime always uses
/// the CONNECTION_STRING environment variable via Program.cs.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING")
            ?? "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString,
                npgsql => npgsql.MigrationsAssembly("TalkingPointsSummary"))
            .Options;

        return new AppDbContext(options);
    }
}
