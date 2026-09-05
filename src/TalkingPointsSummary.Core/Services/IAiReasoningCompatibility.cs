namespace TalkingPointsSummary.Services;

/// <summary>
/// Validates whether a profile's thinking mode can be sent to its model, and explains the
/// failure when it cannot. Compatibility rules are provider- and model-family specific (which
/// modes exist, which models accept which mode, how model families are told apart), so each AI
/// provider supplies its own implementation. The worker resolves whichever one is registered for
/// the configured provider; this interface itself names no vendor or model family.
/// </summary>
public interface IAiReasoningCompatibility
{
    /// <summary>
    /// Reports whether the given thinking mode can be sent to the given model.
    /// </summary>
    /// <param name="modelId">Model identifier from the profile.</param>
    /// <param name="thinking">Thinking mode from the profile.</param>
    /// <returns>
    /// <c>false</c> only when the model is recognized and definitely rejects the mode. A mode
    /// that requests no extended thinking, and a model this implementation cannot recognize,
    /// are always compatible.
    /// </returns>
    bool IsCompatible(string? modelId, string? thinking);

    /// <summary>
    /// Builds the startup validation message for a profile whose model and thinking mode cannot
    /// be used together.
    /// </summary>
    /// <param name="profileName">Profile name as it appears in configuration.</param>
    /// <returns>An operator-facing message naming both keys and the rule.</returns>
    string IncompatibleMessage(string profileName);
}
