using FluentAssertions;
using Microsoft.Extensions.Configuration;
using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Tests;

public class DebugFeaturesOptionsTests
{
    [Fact]
    public void IsEnabled_ReturnsFalse_WhenSettingIsMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        DebugFeaturesOptions.IsEnabled(configuration).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_ReturnsTrue_WhenSettingIsTrue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DebugFeatures:Enabled"] = "true"
            })
            .Build();

        DebugFeaturesOptions.IsEnabled(configuration).Should().BeTrue();
    }
}