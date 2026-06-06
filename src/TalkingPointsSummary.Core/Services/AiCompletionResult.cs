namespace TalkingPointsSummary.Services;

/// <summary>
/// Result returned by a successful AI completion call.
/// </summary>
/// <param name="Text">Generated text from the model.</param>
/// <param name="RawResponse">Raw JSON response from the provider, when available.</param>
public record AiCompletionResult(string Text, string? RawResponse = null);
