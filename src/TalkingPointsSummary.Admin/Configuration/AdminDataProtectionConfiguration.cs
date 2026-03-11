using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TalkingPointsSummary.Admin.Configuration;

/// <summary>
/// Configures persistent DataProtection keys for the admin application.
/// </summary>
public static class AdminDataProtectionConfiguration
{
    /// <summary>
    /// Stable application name used to isolate the admin key ring.
    /// </summary>
    public const string ApplicationName = "TalkingPointsSummary.Admin";

    /// <summary>
    /// Configuration path for the persisted DataProtection key directory.
    /// </summary>
    public const string KeysDirectoryConfigPath = "DataProtection:KeysDirectory";

    /// <summary>
    /// Default in-container directory used for persisted DataProtection keys.
    /// </summary>
    public const string DefaultContainerKeysDirectory = "/var/app/data-protection-keys";

    /// <summary>
    /// Registers DataProtection and persists keys when a durable directory is configured.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configuration">Application configuration source.</param>
    /// <param name="runningInContainer">Whether the app is running inside a container.</param>
    public static void ConfigureDataProtection(
        IServiceCollection services,
        IConfiguration configuration,
        bool runningInContainer)
    {
        var dataProtectionBuilder = services.AddDataProtection()
            .SetApplicationName(ApplicationName);

        var keysDirectory = ResolveKeysDirectory(configuration, runningInContainer);
        if (string.IsNullOrWhiteSpace(keysDirectory))
        {
            return;
        }

        Directory.CreateDirectory(keysDirectory);
        dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
    }

    /// <summary>
    /// Returns the configured key directory, or the container default when running in a container.
    /// </summary>
    /// <param name="configuration">Application configuration source.</param>
    /// <param name="runningInContainer">Whether the app is running inside a container.</param>
    public static string? ResolveKeysDirectory(IConfiguration configuration, bool runningInContainer)
    {
        var configuredDirectory = configuration[KeysDirectoryConfigPath];
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return configuredDirectory;
        }

        return runningInContainer ? DefaultContainerKeysDirectory : null;
    }

    /// <summary>
    /// Returns whether the current process is running in a container.
    /// </summary>
    public static bool IsRunningInContainer()
        => string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
}