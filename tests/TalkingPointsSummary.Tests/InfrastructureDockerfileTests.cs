using FluentAssertions;

namespace TalkingPointsSummary.Tests;

public class InfrastructureDockerfileTests
{
    [Theory]
    [InlineData("infra/Dockerfile")]
    [InlineData("infra/Dockerfile.admin")]
    public void RuntimeDockerfiles_InstallKerberosGssapiDependency(string relativePath)
    {
        var repoRoot = FindRepositoryRoot();
        var dockerfilePath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        File.ReadAllText(dockerfilePath)
            .Should().Contain("libgssapi-krb5-2");
    }

    [Fact]
    public void AdminDockerfile_PublishStepAllowsRestoreAfterFullSourceCopy()
    {
        var repoRoot = FindRepositoryRoot();
        var dockerfilePath = Path.Combine(repoRoot, "infra", "Dockerfile.admin");
        var dockerfile = File.ReadAllText(dockerfilePath);

        dockerfile.Should().Contain("RUN dotnet publish -c Release -o /app/publish");
        dockerfile.Should().NotContain("RUN dotnet publish -c Release -o /app/publish --no-restore");
    }

    [Fact]
    public void DockerCompose_AdminPersistsDataProtectionKeys()
    {
        var repoRoot = FindRepositoryRoot();
        var composePath = Path.Combine(repoRoot, "infra", "docker-compose.yml");
        var compose = File.ReadAllText(composePath);

        compose.Should().Contain("DataProtection__KeysDirectory=/var/app/data-protection-keys");
        compose.Should().Contain("${ADMIN_DATA_PROTECTION_KEYS_PATH:-../runtime-data/admin-data-protection-keys}:/var/app/data-protection-keys");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TalkingPointsSummary.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}