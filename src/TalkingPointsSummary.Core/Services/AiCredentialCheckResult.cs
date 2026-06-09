namespace TalkingPointsSummary.Services;

/// <summary>
/// Result of an AI credential validation probe.
/// </summary>
/// <param name="IsValid">Whether the credentials were accepted by the provider.</param>
/// <param name="IsInconclusive">Whether the result is ambiguous (e.g. server error or rate limit) and cannot confirm key validity.</param>
/// <param name="Reason">Human-readable explanation of the result.</param>
public record AiCredentialCheckResult(bool IsValid, bool IsInconclusive, string Reason);
