using Markdig;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Converts Markdown to HTML using Markdig.
/// </summary>
public class MarkdownConverter : IMarkdownConverter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// Converts Markdown text into HTML using the configured Markdig pipeline.
    /// </summary>
    /// <param name="markdown">Markdown content to convert.</param>
    public string ToHtml(string markdown)
    {
        return Markdown.ToHtml(markdown, Pipeline);
    }
}
