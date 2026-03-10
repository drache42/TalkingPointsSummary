using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.Tests;

public class NewsletterScraperTests
{
    private static NewsletterScraper CreateScraper(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var settings = Options.Create(new AppSettings { BrowserlessUrl = "http://localhost:3000" });
        return new NewsletterScraper(httpClient, settings, NullLogger<NewsletterScraper>.Instance);
    }

    private static Mock<HttpMessageHandler> CreateMockHandler(HttpStatusCode statusCode, string responseBody)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
            });

        return mockHandler;
    }

    [Fact]
    public async Task ScrapeAsync_ValidResponse_ExtractsTextFromNestedJsonPath()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new
                {
                    results = new[]
                    {
                        new { text = "Scraped newsletter content here" }
                    }
                }
            }
        });

        var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseBody);
        var scraper = CreateScraper(mockHandler.Object);

        var result = await scraper.ScrapeAsync("https://www.smore.com/abc");

        result.Should().Be("Scraped newsletter content here");
    }

    [Fact]
    public async Task ScrapeAsync_HttpCallThrows_ReturnsNull()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var scraper = CreateScraper(mockHandler.Object);

        var result = await scraper.ScrapeAsync("https://www.smore.com/abc");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ScrapeAsync_UnexpectedJsonStructure_ReturnsNull()
    {
        // Missing 'data' array
        var responseBody = JsonSerializer.Serialize(new { unexpected = "structure" });

        var mockHandler = CreateMockHandler(HttpStatusCode.OK, responseBody);
        var scraper = CreateScraper(mockHandler.Object);

        var result = await scraper.ScrapeAsync("https://www.smore.com/abc");

        result.Should().BeNull();
    }
}
