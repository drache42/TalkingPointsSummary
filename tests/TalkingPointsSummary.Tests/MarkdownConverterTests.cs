using FluentAssertions;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class MarkdownConverterTests
{
    private readonly MarkdownConverter _converter = new();

    [Fact]
    public void ToHtml_ConvertsHeadings()
    {
        var markdown = "# Hello World";
        var html = _converter.ToHtml(markdown);

        html.Should().Contain("<h1");
        html.Should().Contain("Hello World</h1>");
    }

    [Fact]
    public void ToHtml_ConvertsBoldText()
    {
        var markdown = "This is **bold** text";
        var html = _converter.ToHtml(markdown);

        html.Should().Contain("<strong>bold</strong>");
    }

    [Fact]
    public void ToHtml_ConvertsBulletList()
    {
        var markdown = "- Item 1\n- Item 2\n- Item 3";
        var html = _converter.ToHtml(markdown);

        html.Should().Contain("<ul>");
        html.Should().Contain("<li>Item 1</li>");
        html.Should().Contain("<li>Item 2</li>");
        html.Should().Contain("<li>Item 3</li>");
    }

    [Fact]
    public void ToHtml_ConvertsMultipleSections()
    {
        var markdown = """
            # 🏫 Whole School News
            ### Picture Day
            Picture day is **Friday**.

            # 📚 Clara (Kindergarten)
            ### Art Show
            Clara's art show is next week.
            """;

        var html = _converter.ToHtml(markdown);

        html.Should().Contain("Whole School News");
        html.Should().Contain("Picture Day");
        html.Should().Contain("Clara (Kindergarten)");
    }
}
