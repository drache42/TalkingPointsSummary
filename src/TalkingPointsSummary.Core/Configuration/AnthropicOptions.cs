using System.ComponentModel.DataAnnotations;

namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Configuration values used when calling the Anthropic API.
/// </summary>
public sealed class AnthropicOptions
{
    /// <summary>
    /// Configuration section name for Anthropic settings.
    /// </summary>
    public const string SectionName = "Anthropic";

    /// <summary>
    /// API key used to authenticate Anthropic requests.
    /// </summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;
}