namespace TalkingPointsSummary.Services;

/// <summary>
/// Result returned by a successful AI completion call.
/// </summary>
/// <param name="Text">Generated text from the model.</param>
/// <param name="RawResponse">Raw JSON response from the provider, when available.</param>
/// <param name="StopReason">
/// Provider-reported reason the model stopped generating, when available. A value of
/// "max_tokens" indicates the response was truncated.
/// </param>
/// <param name="Usage">Token usage reported by the provider, when available.</param>
public record AiCompletionResult(
    string Text,
    string? RawResponse = null,
    string? StopReason = null,
    AiTokenUsage? Usage = null);

/// <summary>
/// Token counts reported by the provider for a single completion call.
/// All values are null when the provider did not report them.
/// </summary>
/// <param name="InputTokens">Number of input tokens billed for the request.</param>
/// <param name="OutputTokens">Number of output tokens generated, including thinking tokens.</param>
/// <param name="ThinkingTokens">Number of tokens spent on extended thinking.</param>
/// <param name="CacheCreationInputTokens">Number of input tokens written to the prompt cache.</param>
/// <param name="CacheReadInputTokens">Number of input tokens served from the prompt cache.</param>
public record AiTokenUsage(
    int? InputTokens = null,
    int? OutputTokens = null,
    int? ThinkingTokens = null,
    int? CacheCreationInputTokens = null,
    int? CacheReadInputTokens = null);
