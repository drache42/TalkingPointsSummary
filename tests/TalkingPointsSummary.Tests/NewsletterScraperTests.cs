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

/// <summary>
/// Verifies newsletter scraping behavior and fallback paths.
/// </summary>
public class NewsletterScraperTests
{
    private static NewsletterScraper CreateScraper(
        HttpMessageHandler handler,
        NewsletterScrapingSecurityOptions? securityOptions = null,
        IHostAddressResolver? hostAddressResolver = null)
    {
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new BrowserlessOptions { BaseUrl = "http://localhost:3000" });
        var security = Options.Create(securityOptions ?? new NewsletterScrapingSecurityOptions());
        var validator = new NewsletterUrlValidator(
            security,
            hostAddressResolver ?? new StubHostAddressResolver([IPAddress.Parse("93.184.216.34")]));
        return new NewsletterScraper(httpClient, options, validator, NullLogger<NewsletterScraper>.Instance);
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

    /// <summary>
    /// Verifies that a valid Browserless response yields extracted newsletter text.
    /// </summary>
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

    /// <summary>
    /// Verifies that scraper HTTP failures return <see langword="null"/>.
    /// </summary>
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

    /// <summary>
    /// Verifies that unexpected Browserless JSON returns <see langword="null"/>.
    /// </summary>
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

    /// <summary>
    /// Verifies that loopback URLs are blocked before Browserless is called.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_LoopbackUrl_ReturnsNullWithoutCallingBrowserless()
    {
        var mockHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var scraper = CreateScraper(mockHandler.Object);

        var result = await scraper.ScrapeAsync("http://127.0.0.1:8080/private");

        result.Should().BeNull();
        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    /// <summary>
    /// Verifies that explicitly allowed HTTP hosts are still scraped.
    /// </summary>
    [Fact]
    public async Task ScrapeAsync_AllowedHostOverHttp_CallsBrowserless()
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
        var scraper = CreateScraper(
            mockHandler.Object,
            new NewsletterScrapingSecurityOptions
            {
                Enabled = true,
                RequireHttps = true,
                AllowedHosts = ["host.docker.internal"],
                AllowHttpHosts = ["host.docker.internal"]
            });

        var result = await scraper.ScrapeAsync("http://host.docker.internal:5001/newsletter.html");

        result.Should().Be("Scraped newsletter content here");
    }

    private sealed class StubHostAddressResolver : IHostAddressResolver
    {
        private readonly IPAddress[] _addresses;

        public StubHostAddressResolver(IPAddress[]? addresses = null)
        {
            _addresses = addresses ?? [];
        }

        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
            => Task.FromResult(_addresses);
    }
}
