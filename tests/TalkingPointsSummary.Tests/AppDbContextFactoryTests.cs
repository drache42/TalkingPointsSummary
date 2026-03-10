using System.Text.Json;
using FluentAssertions;
using TalkingPointsSummary.Data;

namespace TalkingPointsSummary.Tests;

public class AppDbContextFactoryTests
{
    [Fact]
    public void CreateDbContext_WhenConnectionStringExists_CreatesContext()
    {
        using var tempDir = new TemporaryDirectory();
        WriteAppSettings(tempDir.Path, new
        {
            ConnectionStrings = new
            {
                TalkingPoints = "Host=localhost;Database=talkingpoints;Username=postgres;Password=postgres"
            }
        });

        using var scope = new CurrentDirectoryScope(tempDir.Path);
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");

        var factory = new AppDbContextFactory();
        var context = factory.CreateDbContext([]);

        context.Should().NotBeNull();
    }

    [Fact]
    public void CreateDbContext_WhenConnectionStringMissing_ThrowsClearError()
    {
        using var tempDir = new TemporaryDirectory();
        WriteAppSettings(tempDir.Path, new { });

        using var scope = new CurrentDirectoryScope(tempDir.Path);
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");

        var factory = new AppDbContextFactory();
        var act = () => factory.CreateDbContext([]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:TalkingPoints*");
    }

    private static void WriteAppSettings(string path, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        File.WriteAllText(System.IO.Path.Combine(path, "appsettings.json"), json);
    }

    private sealed class CurrentDirectoryScope : IDisposable
    {
        private readonly string _originalDirectory;
        private readonly string? _originalDotnetEnvironment;
        private readonly string? _originalAspnetcoreEnvironment;
        private readonly string? _originalConnectionString;

        public CurrentDirectoryScope(string newDirectory)
        {
            _originalDirectory = Directory.GetCurrentDirectory();
            _originalDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            _originalAspnetcoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            _originalConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__TalkingPoints");
            Directory.SetCurrentDirectory(newDirectory);
            Environment.SetEnvironmentVariable("ConnectionStrings__TalkingPoints", null);
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_originalDirectory);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _originalDotnetEnvironment);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _originalAspnetcoreEnvironment);
            Environment.SetEnvironmentVariable("ConnectionStrings__TalkingPoints", _originalConnectionString);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tps-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}