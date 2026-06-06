namespace TalkingPointsSummary.Services;

/// <summary>
/// Parameters for a single AI completion call.
/// </summary>
/// <param name="Prompt">The user prompt text to send to the model.</param>
/// <param name="ModelId">Provider-specific model identifier.</param>
/// <param name="MaxTokens">Maximum number of tokens the model may return.</param>
public record AiCompletionRequest(string Prompt, string ModelId, int MaxTokens);
