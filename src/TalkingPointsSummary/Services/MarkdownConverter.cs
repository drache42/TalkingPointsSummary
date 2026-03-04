using Markdig;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Converts Markdown to HTML using Markdig.
/// </summary>
public class MarkdownConverter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public string ToHtml(string markdown)
    {
        return Markdown.ToHtml(markdown, Pipeline);
    }
}
