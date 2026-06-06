namespace TalkingPointsSummary.Services;

/// <summary>
/// Provider-agnostic client for calling an AI completion API.
/// </summary>
public interface IAiClient
{
    /// <summary>
    /// Sends a completion request and returns the generated text.
    /// </summary>
    /// <param name="request">Request parameters including prompt, model, and token limit.</param>
    /// <param name="ct">Token used to cancel the request.</param>
    Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Probes the configured provider to verify that credentials are valid.
    /// </summary>
    /// <param name="ct">Token used to cancel the probe.</param>
    Task<AiCredentialCheckResult> ValidateCredentialsAsync(CancellationToken ct = default);
}
