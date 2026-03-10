using Microsoft.Extensions.Configuration;

namespace TalkingPointsSummary.Configuration;

public sealed class DebugFeaturesOptions
{
    public const string SectionName = "DebugFeatures";
    public const string EnabledKey = SectionName + ":Enabled";

    public bool Enabled { get; init; }

    public static bool IsEnabled(IConfiguration configuration)
        => bool.TryParse(configuration[EnabledKey], out var enabled) && enabled;
}