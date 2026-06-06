using System.ComponentModel.DataAnnotations;

namespace TalkingPointsSummary.Configuration;

/// <summary>
/// Top-level AI provider configuration.
/// </summary>
public sealed class AiOptions
{
    /// <summary>
    /// Configuration section name for AI settings.
    /// </summary>
    public const string SectionName = "Ai";

    /// <summary>
    /// Active AI provider name (e.g. "Anthropic").
    /// </summary>
    [Required]
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Anthropic-specific provider options.
    /// </summary>
    public AnthropicProviderOptions Anthropic { get; set; } = new();

    /// <summary>
    /// Named profiles that select model and token limits per use case.
    /// </summary>
    public AiProfilesOptions Profiles { get; set; } = new();
}

/// <summary>
/// Anthropic-specific connection options.
/// </summary>
public sealed class AnthropicProviderOptions
{
    /// <summary>
    /// API key used to authenticate Anthropic requests.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL for the Anthropic API.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>
    /// Anthropic API version header value.
    /// </summary>
    public string ApiVersion { get; set; } = "2023-06-01";
}

/// <summary>
/// Named AI profiles used by each call site.
/// </summary>
public sealed class AiProfilesOptions
{
    /// <summary>
    /// Profile used for message categorization.
    /// </summary>
    public AiProfileOptions Categorization { get; set; } = new() { ModelId = "claude-haiku-4-5-20251001", MaxTokens = 1024 };

    /// <summary>
    /// Profile used for weekly summary generation.
    /// </summary>
    public AiProfileOptions Summarization { get; set; } = new() { ModelId = "claude-sonnet-4-5-20250929", MaxTokens = 8192 };

    /// <summary>
    /// Profile used for credential validation probes.
    /// </summary>
    public AiProfileOptions Validation { get; set; } = new() { ModelId = "claude-haiku-3-5-20241022", MaxTokens = 1 };
}

/// <summary>
/// Model identifier and token limit for a single AI use-case profile.
/// </summary>
public sealed class AiProfileOptions
{
    /// <summary>
    /// Model identifier passed to the provider.
    /// </summary>
    [Required]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of tokens the model may return.
    /// </summary>
    public int MaxTokens { get; set; }
}
