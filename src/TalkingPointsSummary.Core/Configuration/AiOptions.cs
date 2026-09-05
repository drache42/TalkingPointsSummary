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
    public AiProfileOptions Categorization { get; set; } = new()
    {
        ModelId = "claude-haiku-4-5-20251001",
        MaxTokens = 1024,
        Thinking = AiThinkingModes.None
    };

    /// <summary>
    /// Profile used for weekly summary generation.
    /// </summary>
    public AiProfileOptions Summarization { get; set; } = new()
    {
        ModelId = "claude-sonnet-5",
        MaxTokens = 32000,
        Thinking = AiThinkingModes.Adaptive,
        Effort = AiEffortLevels.High
    };

    /// <summary>
    /// Profile used for credential validation probes.
    /// </summary>
    public AiProfileOptions Validation { get; set; } = new()
    {
        ModelId = "claude-haiku-4-5-20251001",
        MaxTokens = 1,
        Thinking = AiThinkingModes.None
    };
}

/// <summary>
/// Supported values for <see cref="AiProfileOptions.Thinking"/>.
/// </summary>
public static class AiThinkingModes
{
    /// <summary>
    /// No extended thinking is requested; the thinking parameter is omitted.
    /// </summary>
    public const string None = "none";

    /// <summary>
    /// Adaptive thinking, where the model decides how much to think. Used by the
    /// Claude 5 model family, which rejects the fixed-budget thinking mode.
    /// </summary>
    public const string Adaptive = "adaptive";

    /// <summary>
    /// Fixed-budget extended thinking, driven by
    /// <see cref="AiProfileOptions.ThinkingBudgetTokens"/>. Used by Claude 4.5 and earlier,
    /// which does not support adaptive thinking or the effort parameter.
    /// </summary>
    public const string Budget = "budget";

    /// <summary>
    /// All supported thinking mode values.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [None, Adaptive, Budget];
}

/// <summary>
/// Supported values for <see cref="AiProfileOptions.Effort"/>.
/// </summary>
public static class AiEffortLevels
{
    /// <summary>
    /// Lowest reasoning effort.
    /// </summary>
    public const string Low = "low";

    /// <summary>
    /// Moderate reasoning effort.
    /// </summary>
    public const string Medium = "medium";

    /// <summary>
    /// High reasoning effort.
    /// </summary>
    public const string High = "high";

    /// <summary>
    /// Extra-high reasoning effort.
    /// </summary>
    public const string XHigh = "xhigh";

    /// <summary>
    /// Maximum reasoning effort.
    /// </summary>
    public const string Max = "max";

    /// <summary>
    /// All supported effort level values.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Low, Medium, High, XHigh, Max];
}

/// <summary>
/// <see cref="AiProfileOptions"/> fields whose value only has meaning under one specific
/// <see cref="AiProfileOptions.Thinking"/> mode. Set under any other mode, nothing sends the
/// value anywhere; it is a configuration mistake, not a value with a different effect.
/// </summary>
public enum AiReasoningField
{
    /// <summary>
    /// <see cref="AiProfileOptions.Effort"/>, meaningful only under <see cref="AiThinkingModes.Adaptive"/>.
    /// </summary>
    Effort,

    /// <summary>
    /// <see cref="AiProfileOptions.ThinkingBudgetTokens"/>, meaningful only under
    /// <see cref="AiThinkingModes.Budget"/>.
    /// </summary>
    ThinkingBudgetTokens
}

/// <summary>
/// Declares which single <see cref="AiThinkingModes"/> value each <see cref="AiReasoningField"/>
/// requires. This is a fact about the app's own normalized profile schema, true for any AI
/// provider, not a provider compatibility rule -- it says nothing about which models exist or
/// which shapes a given vendor's API accepts.
/// </summary>
public static class AiReasoningFieldRules
{
    private static readonly IReadOnlyDictionary<AiReasoningField, string> RequiredThinkingMode =
        new Dictionary<AiReasoningField, string>
        {
            [AiReasoningField.Effort] = AiThinkingModes.Adaptive,
            [AiReasoningField.ThinkingBudgetTokens] = AiThinkingModes.Budget
        };

    /// <summary>
    /// Every field this table has a rule for, so a caller checking "is each field used correctly"
    /// can loop over the table's own keys instead of hardcoding a second copy of the field list.
    /// </summary>
    public static readonly IReadOnlyList<AiReasoningField> AllFields = [.. RequiredThinkingMode.Keys];

    /// <summary>
    /// The one <see cref="AiThinkingModes"/> value under which <paramref name="field"/> is used.
    /// </summary>
    public static string RequiredModeFor(AiReasoningField field) => RequiredThinkingMode[field];

    /// <summary>
    /// Reports whether <paramref name="field"/> is meaningful under the given thinking mode.
    /// </summary>
    public static bool AppliesTo(AiReasoningField field, string? thinking) =>
        string.Equals(RequiredThinkingMode[field], thinking, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Model identifier, token limit, and reasoning settings for a single AI use-case profile.
/// </summary>
public sealed class AiProfileOptions
{
    /// <summary>
    /// Model identifier passed to the provider.
    /// </summary>
    [Required]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of tokens the model may return. When extended thinking is enabled
    /// the thinking tokens count against this limit, so thinking profiles need a larger value.
    /// </summary>
    public int MaxTokens { get; set; }

    /// <summary>
    /// Extended thinking mode for this profile. One of the values in
    /// <see cref="AiThinkingModes"/>: "none", "adaptive", or "budget".
    /// </summary>
    public string Thinking { get; set; } = AiThinkingModes.None;

    /// <summary>
    /// Thinking token budget, used only when <see cref="Thinking"/> is "budget".
    /// The provider requires at least 1024 tokens, and the budget must be smaller
    /// than <see cref="MaxTokens"/>.
    /// </summary>
    public int ThinkingBudgetTokens { get; set; }

    /// <summary>
    /// Reasoning effort level for this profile. One of the values in
    /// <see cref="AiEffortLevels"/>: "low", "medium", "high", "xhigh", or "max".
    /// Null omits the effort parameter from the request.
    /// </summary>
    public string? Effort { get; set; }
}
