namespace TalkingPointsSummary.Services;

/// <summary>
/// Response payload returned by the Anthropic messages API.
/// </summary>
public class AnthropicResponse
{
    /// <summary>
    /// Content blocks returned by Anthropic.
    /// </summary>
    public List<AnthropicContent>? Content { get; set; }
}
