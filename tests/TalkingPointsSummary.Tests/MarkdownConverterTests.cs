using FluentAssertions;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class MarkdownConverterTests
{
    private readonly MarkdownConverter _converter = new();

    [Fact]
    public void ToHtml_NonEmptyInput_ReturnsNonEmptyString()
    {
        var html = _converter.ToHtml("# Hello World");
        html.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ToHtml_EmptyInput_ReturnsEmptyOrWhitespace()
    {
        var html = _converter.ToHtml(string.Empty);
        html.Should().BeEmpty();
    }
}
