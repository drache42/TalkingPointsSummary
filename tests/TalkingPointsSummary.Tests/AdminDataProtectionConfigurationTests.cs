using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TalkingPointsSummary.Admin.Configuration;

namespace TalkingPointsSummary.Tests;

public class AdminDataProtectionConfigurationTests
{
    [Fact]
    public void ResolveKeysDirectory_ReturnsConfiguredDirectory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AdminDataProtectionConfiguration.KeysDirectoryConfigPath] = "/custom/key-ring"
            })
            .Build();

        var keysDirectory = AdminDataProtectionConfiguration.ResolveKeysDirectory(configuration, runningInContainer: false);

        keysDirectory.Should().Be("/custom/key-ring");
    }

    [Fact]
    public void ResolveKeysDirectory_UsesContainerDefaultWhenUnset()
    {
        var configuration = new ConfigurationBuilder().Build();

        var keysDirectory = AdminDataProtectionConfiguration.ResolveKeysDirectory(configuration, runningInContainer: true);

        keysDirectory.Should().Be(AdminDataProtectionConfiguration.DefaultContainerKeysDirectory);
    }

    [Fact]
    public void ConfigureDataProtection_PersistsKeysToConfiguredDirectory()
    {
        var keysDirectory = Path.Combine(Path.GetTempPath(), "tps-admin-dp-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AdminDataProtectionConfiguration.KeysDirectoryConfigPath] = keysDirectory
                })
                .Build();

            AdminDataProtectionConfiguration.ConfigureDataProtection(services, configuration, runningInContainer: false);

            using var provider = services.BuildServiceProvider();
            var dataProtectionProvider = provider.GetRequiredService<IDataProtectionProvider>();
            var protector = dataProtectionProvider.CreateProtector("tests.admin.data-protection");

            protector.Protect("payload").Should().NotBeNullOrWhiteSpace();
            Directory.Exists(keysDirectory).Should().BeTrue();
            Directory.GetFiles(keysDirectory, "*.xml").Should().NotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(keysDirectory))
            {
                Directory.Delete(keysDirectory, recursive: true);
            }
        }
    }
}