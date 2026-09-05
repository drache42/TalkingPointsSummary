namespace TalkingPointsSummary.Services;

/// <summary>
/// Parameters for a single AI completion call.
/// </summary>
/// <param name="Prompt">The user prompt text to send to the model.</param>
/// <param name="ModelId">Provider-specific model identifier.</param>
/// <param name="MaxTokens">Maximum number of tokens the model may return.</param>
/// <param name="Thinking">
/// Extended thinking mode: "none", "adaptive", or "budget". Defaults to "none".
/// </param>
/// <param name="ThinkingBudgetTokens">
/// Thinking token budget, used only when <paramref name="Thinking"/> is "budget".
/// </param>
/// <param name="Effort">
/// Reasoning effort level ("low", "medium", "high", "xhigh", "max"). Null omits the parameter.
/// </param>
public record AiCompletionRequest(
    string Prompt,
    string ModelId,
    int MaxTokens,
    string Thinking = "none",
    int ThinkingBudgetTokens = 0,
    string? Effort = null);
