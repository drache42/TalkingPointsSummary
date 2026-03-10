using Microsoft.Extensions.Configuration;

namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Configuration values that gate optional debug-only endpoints and features.
/// </summary>
public sealed class DebugFeaturesOptions
{
    /// <summary>
    /// Configuration section name for debug feature settings.
    /// </summary>
    public const string SectionName = "DebugFeatures";

    /// <summary>
    /// Fully-qualified configuration key for the enabled flag.
    /// </summary>
    public const string EnabledKey = SectionName + ":Enabled";

    /// <summary>
    /// Whether debug-only features are enabled.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Returns whether debug features are enabled in the supplied configuration.
    /// </summary>
    /// <param name="configuration">Configuration source to read.</param>
    public static bool IsEnabled(IConfiguration configuration)
        => bool.TryParse(configuration[EnabledKey], out var enabled) && enabled;
}