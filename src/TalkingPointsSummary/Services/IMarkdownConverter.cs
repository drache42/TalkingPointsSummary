namespace TalkingPointsSummary.Services;

public interface IMarkdownConverter
{
    string ToHtml(string markdown);
}
