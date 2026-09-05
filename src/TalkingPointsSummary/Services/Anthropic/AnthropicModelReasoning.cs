using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Extended-thinking request shape a Claude model family accepts. The two shapes are mutually
/// exclusive: sending the wrong one is rejected by the provider with HTTP 400 on every call,
/// not degraded, so the pairing has to be decided before a request is built.
/// </summary>
internal enum AnthropicReasoningShape
{
    /// <summary>
    /// The model identifier is not a recognized Claude id, so no constraint can be asserted and
    /// whatever the profile configures is sent through unchanged. Gateway aliases land here.
    /// </summary>
    Unknown,

    /// <summary>
    /// Claude 5 and later: thinking is requested as <c>{"type":"adaptive"}</c> and the reasoning
    /// level travels in <c>output_config.effort</c>. These models reject <c>{"type":"enabled"}</c>.
    /// </summary>
    Adaptive,

    /// <summary>
    /// Claude 4.5 and earlier: thinking is requested as
    /// <c>{"type":"enabled","budget_tokens":N}</c>. These models support neither adaptive
    /// thinking nor the effort parameter.
    /// </summary>
    Budget
}

/// <summary>
/// Anthropic's <see cref="IAiReasoningCompatibility"/>. Maps a Claude model identifier to the
/// extended-thinking shape its family accepts, so a profile that pairs a model with the wrong
/// reasoning mode is caught at startup instead of failing every request at runtime. Every rule in
/// this class is specific to how Claude names and versions its models; a different provider's
/// implementation of the interface would carry none of it.
/// </summary>
internal sealed class AnthropicModelReasoning : IAiReasoningCompatibility
{
    /// <summary>
    /// First Claude major version that uses adaptive thinking and reasoning effort.
    /// </summary>
    private const int FirstAdaptiveMajorVersion = 5;

    /// <summary>
    /// Longest numeric token still read as a version number. Anything longer is a release date
    /// such as "20251001", which must never be mistaken for a major version.
    /// </summary>
    private const int MaxVersionTokenLength = 2;

    private const string ClaudeMarker = "claude";

    private static readonly string[] FamilyNames = ["opus", "sonnet", "haiku"];

    /// <summary>
    /// Returns the extended-thinking shape the given model accepts.
    /// </summary>
    /// <param name="modelId">
    /// Model identifier as configured, optionally carrying a gateway prefix such as
    /// "anthropic/" or "us.anthropic.".
    /// </param>
    /// <returns>
    /// The shape for a recognized Claude id, or <see cref="AnthropicReasoningShape.Unknown"/>
    /// when the id carries no family and version this code can read.
    /// </returns>
    public static AnthropicReasoningShape GetShape(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return AnthropicReasoningShape.Unknown;

        var markerIndex = modelId.IndexOf(ClaudeMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return AnthropicReasoningShape.Unknown;

        // "claude-3-5-sonnet-20241022" puts the version before the family and
        // "claude-sonnet-5" puts it after, so both parts are located independently.
        var tokens = modelId[(markerIndex + ClaudeMarker.Length)..]
            .Split(['-', '.', '_', ':'], StringSplitOptions.RemoveEmptyEntries);

        var hasFamily = false;
        var majorVersion = 0;

        foreach (var token in tokens)
        {
            if (!hasFamily && FamilyNames.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                hasFamily = true;
                continue;
            }

            if (majorVersion == 0 && IsVersionToken(token))
            {
                majorVersion = int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (!hasFamily || majorVersion == 0)
            return AnthropicReasoningShape.Unknown;

        return majorVersion >= FirstAdaptiveMajorVersion
            ? AnthropicReasoningShape.Adaptive
            : AnthropicReasoningShape.Budget;
    }

    /// <inheritdoc/>
    public bool IsCompatible(string? modelId, string? thinking)
    {
        var shape = GetShape(modelId);
        if (shape == AnthropicReasoningShape.Unknown)
            return true;

        if (string.Equals(thinking, AiThinkingModes.Adaptive, StringComparison.OrdinalIgnoreCase))
            return shape == AnthropicReasoningShape.Adaptive;

        if (string.Equals(thinking, AiThinkingModes.Budget, StringComparison.OrdinalIgnoreCase))
            return shape == AnthropicReasoningShape.Budget;

        // "none" sends no thinking parameter at all, and an unrecognized mode string is the
        // thinking-mode validator's problem, not this one's.
        return true;
    }

    /// <inheritdoc/>
    public string IncompatibleMessage(string profileName) =>
        $"Ai:Profiles:{profileName}:Thinking is not supported by Ai:Profiles:{profileName}:ModelId. "
        + $"Claude 5 and later models accept only '{AiThinkingModes.Adaptive}' (with Effort); "
        + $"Claude 4.5 and earlier accept only '{AiThinkingModes.Budget}' (with ThinkingBudgetTokens). "
        + $"'{AiThinkingModes.None}' works with either.";

    private static bool IsVersionToken(string token)
    {
        if (token.Length is 0 or > MaxVersionTokenLength)
            return false;

        foreach (var character in token)
        {
            if (!char.IsAsciiDigit(character))
                return false;
        }

        return true;
    }
}
