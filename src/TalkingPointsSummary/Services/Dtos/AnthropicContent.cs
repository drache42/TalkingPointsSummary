namespace TalkingPointsSummary.Services;

/// <summary>
/// Content block returned by the Anthropic messages API.
/// </summary>
public class AnthropicContent
{
    /// <summary>
    /// Content block type reported by Anthropic.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Text payload for the content block.
    /// </summary>
    public string Text { get; set; } = string.Empty;
}
