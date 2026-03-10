namespace TalkingPointsSummary.Services;

/// <summary>
/// Converts Markdown summary content into HTML for email delivery.
/// </summary>
public interface IMarkdownConverter
{
    /// <summary>
    /// Converts Markdown text into HTML.
    /// </summary>
    /// <param name="markdown">Markdown content to convert.</param>
    string ToHtml(string markdown);
}
