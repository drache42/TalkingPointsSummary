using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;
using TalkingPointsSummary.Services;

namespace TalkingPointsSummary.IntegrationTests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public class NewsletterScraperLiveTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public NewsletterScraperLiveTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ScrapeAsync_RealBrowserless_ReturnsBodyText()
    {
        // Arrange: serve a page on the content server
        _fixture.RegisterContentPage("scrape-test.html", "<body><p>Hello from the newsletter</p></body>");

        await using var sp = _fixture.CreateServiceProvider();
        var scraper = sp.GetRequiredService<INewsletterScraper>();

        // Act: scrape via real Browserless
        var url = $"{_fixture.ContentServerUrl}/scrape-test.html";
        var result = await scraper.ScrapeAsync(url, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().Contain("Hello from the newsletter");
    }

    [Fact]
    public async Task ScrapeAsync_BrowserlessReturnsHttpError_ReturnsNull()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("gateway error", Encoding.UTF8, "text/plain")
            }));
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var scraper = new NewsletterScraper(
            httpClient,
            Options.Create(new BrowserlessOptions { BaseUrl = "http://browserless.test" }),
            new NewsletterUrlValidator(
                Options.Create(new NewsletterScrapingSecurityOptions()),
                new StubHostAddressResolver(Array.Empty<IPAddress>())),
            loggerFactory.CreateLogger<NewsletterScraper>());

        // Act
        var url = "https://example.test/does-not-exist.html";
        var result = await scraper.ScrapeAsync(url, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class StubHostAddressResolver : IHostAddressResolver
    {
        private readonly IPAddress[] _addresses;

        public StubHostAddressResolver(IPAddress[] addresses)
        {
            _addresses = addresses;
        }

        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
            => Task.FromResult(_addresses);
    }
}
