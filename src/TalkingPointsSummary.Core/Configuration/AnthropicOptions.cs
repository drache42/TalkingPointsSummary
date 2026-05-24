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

    /// <summary>
    /// Anthropic model used for full weekly summary generation.
    /// </summary>
    public string SummaryModel { get; set; } = "claude-sonnet-4-5-20250929";

    /// <summary>
    /// Anthropic model used for fast message categorization.
    /// </summary>
    public string CategorizationModel { get; set; } = "claude-haiku-4-5-20251001";
}