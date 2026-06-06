namespace TalkingPointsSummary.Services;

/// <summary>
/// Result of an AI credential validation probe.
/// </summary>
/// <param name="IsValid">Whether the credentials were accepted by the provider.</param>
/// <param name="Reason">Human-readable explanation of the result.</param>
public record AiCredentialCheckResult(bool IsValid, string Reason);
