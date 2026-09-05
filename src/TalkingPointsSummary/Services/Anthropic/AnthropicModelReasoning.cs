using System.Globalization;
using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Extended-thinking request shape a Claude model accepts. Adaptive and Budget are mutually
/// exclusive: sending the wrong one is rejected by the provider with HTTP 400 on every call, not
/// degraded, so the pairing has to be decided before a request is built. Either means the
/// provider currently accepts both, which happens only during Opus/Sonnet 4.6's transitional
/// window where the older shape is deprecated but not yet removed.
/// </summary>
internal enum AnthropicReasoningShape
{
    /// <summary>
    /// The model identifier is not a recognized Claude id, so no constraint can be asserted and
    /// whatever the profile configures is sent through unchanged. Gateway aliases land here.
    /// </summary>
    Unknown,

    /// <summary>
    /// Thinking is requested as <c>{"type":"adaptive"}</c> and the reasoning level travels in
    /// <c>output_config.effort</c>. These models reject <c>{"type":"enabled"}</c> with HTTP 400.
    /// </summary>
    Adaptive,

    /// <summary>
    /// Thinking is requested as <c>{"type":"enabled","budget_tokens":N}</c>. These models do not
    /// accept adaptive thinking or the effort parameter.
    /// </summary>
    Budget,

    /// <summary>
    /// Both shapes are currently accepted. Opus and Sonnet 4.6 only: budget_tokens still works
    /// there as a deprecated transitional escape hatch alongside the recommended adaptive shape.
    /// </summary>
    Either
}

/// <summary>
/// Anthropic's <see cref="IAiReasoningCompatibility"/>. Maps a Claude model identifier to the
/// extended-thinking shape its family and version accept, so a profile that pairs a model with an
/// impossible reasoning mode is caught at startup instead of failing every request at runtime.
/// Every rule in this class is specific to how Claude names and versions its models; a different
/// provider's implementation of the interface would carry none of it.
/// </summary>
/// <remarks>
/// The adaptive rollout is not a clean "major version N and later" cutoff: within the Opus and
/// Sonnet families, 4.6 accepts either shape, 4.7 and later accept only adaptive, and every
/// version below 4.6 accepts only budget. Haiku has no adaptive-capable release yet, so a Haiku 4.x
/// id stays budget-only even though Opus/Sonnet 4.6+ do not. This is why the shape is computed
/// from family, major, and minor version together rather than from major version alone.
/// </remarks>
internal sealed class AnthropicModelReasoning : IAiReasoningCompatibility
{
    /// <summary>
    /// First major version, across every family, that accepts only adaptive thinking.
    /// </summary>
    private const int FirstAdaptiveOnlyMajorVersion = 5;

    /// <summary>
    /// The Opus/Sonnet major version whose shape depends on the minor version rather than being
    /// fixed for the whole major version, unlike every other major version.
    /// </summary>
    private const int MixedShapeMajorVersion = 4;

    /// <summary>
    /// Opus/Sonnet minor version that still accepts budget_tokens as a deprecated transitional
    /// escape hatch alongside the recommended adaptive shape.
    /// </summary>
    private const int TransitionalMinorVersion = 6;

    /// <summary>
    /// First Opus/Sonnet minor version, within <see cref="MixedShapeMajorVersion"/>, where
    /// budget_tokens is removed and only adaptive thinking is accepted.
    /// </summary>
    private const int FirstAdaptiveOnlyMinorVersion = 7;

    /// <summary>
    /// Longest numeric token still read as a version number. Anything longer is a release date
    /// such as "20251001", which must never be mistaken for a version number.
    /// </summary>
    private const int MaxVersionTokenLength = 2;

    private const string ClaudeMarker = "claude";
    private const string HaikuFamily = "haiku";

    private static readonly string[] FamilyNames = ["opus", "sonnet", HaikuFamily];

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

        // "claude-3-5-sonnet-20241022" puts major.minor before the family and
        // "claude-opus-4-7" puts them after, so family, major, and minor are each located
        // independently of where the others appear in the token stream.
        var tokens = modelId[(markerIndex + ClaudeMarker.Length)..]
            .Split(['-', '.', '_', ':'], StringSplitOptions.RemoveEmptyEntries);

        string? family = null;
        // -1, not 0: a version token can legitimately parse to 0, so 0 cannot double as the
        // "not found yet" sentinel without colliding with that real value.
        var majorVersion = -1;
        var minorVersion = -1;

        foreach (var token in tokens)
        {
            if (family is null)
            {
                var matchedFamily = Array.Find(FamilyNames,
                    f => string.Equals(f, token, StringComparison.OrdinalIgnoreCase));
                if (matchedFamily is not null)
                {
                    family = matchedFamily;
                    continue;
                }
            }

            if (!IsVersionToken(token))
                continue;

            if (majorVersion == -1)
                majorVersion = int.Parse(token, CultureInfo.InvariantCulture);
            else if (minorVersion == -1)
                minorVersion = int.Parse(token, CultureInfo.InvariantCulture);
        }

        if (family is null || majorVersion == -1)
            return AnthropicReasoningShape.Unknown;

        if (majorVersion >= FirstAdaptiveOnlyMajorVersion)
            return AnthropicReasoningShape.Adaptive;

        // Haiku has no adaptive-capable release yet, so it never enters the mixed-shape branch
        // that applies to Opus and Sonnet's 4.x generation.
        var isHaiku = string.Equals(family, HaikuFamily, StringComparison.OrdinalIgnoreCase);
        if (!isHaiku && majorVersion == MixedShapeMajorVersion)
        {
            // Opus/Sonnet 4.x is the one case where the shape genuinely depends on the minor
            // version rather than being fixed for the whole major version. An id with no readable
            // minor (a bare "opus-4", or an alias this parser cannot resolve to a specific
            // release) does not fall into a default shape here -- there is no version-independent
            // answer for this major version, so it is Unknown rather than a confident guess.
            if (minorVersion == -1)
                return AnthropicReasoningShape.Unknown;

            if (minorVersion >= FirstAdaptiveOnlyMinorVersion)
                return AnthropicReasoningShape.Adaptive;

            if (minorVersion == TransitionalMinorVersion)
                return AnthropicReasoningShape.Either;
        }

        return AnthropicReasoningShape.Budget;
    }

    /// <inheritdoc/>
    public bool IsCompatible(string? modelId, string? thinking)
    {
        var shape = GetShape(modelId);
        if (shape == AnthropicReasoningShape.Unknown)
            return true;

        if (string.Equals(thinking, AiThinkingModes.Adaptive, StringComparison.OrdinalIgnoreCase))
            return shape is AnthropicReasoningShape.Adaptive or AnthropicReasoningShape.Either;

        if (string.Equals(thinking, AiThinkingModes.Budget, StringComparison.OrdinalIgnoreCase))
            return shape is AnthropicReasoningShape.Budget or AnthropicReasoningShape.Either;

        // "none" sends no thinking parameter at all, and an unrecognized mode string is the
        // thinking-mode validator's problem, not this one's.
        return true;
    }

    /// <inheritdoc/>
    public string IncompatibleMessage(string profileName) =>
        $"Ai:Profiles:{profileName}:Thinking is not supported by Ai:Profiles:{profileName}:ModelId. "
        + $"Claude 5 and later, and Opus/Sonnet 4.7 and later, accept only '{AiThinkingModes.Adaptive}' "
        + $"(with Effort). Claude 4.5 and earlier, and Haiku, accept only '{AiThinkingModes.Budget}' "
        + $"(with ThinkingBudgetTokens). Opus/Sonnet 4.6 accept either. "
        + $"'{AiThinkingModes.None}' works with any model.";

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
