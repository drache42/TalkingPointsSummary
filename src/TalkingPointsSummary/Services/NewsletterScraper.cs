using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Scrapes newsletter content via an external Browserless service.
/// </summary>
public class NewsletterScraper : INewsletterScraper
{
    private readonly HttpClient _httpClient;
    private readonly BrowserlessOptions _browserless;
    private readonly INewsletterUrlValidator _newsletterUrlValidator;
    private readonly ILogger<NewsletterScraper> _logger;

    /// <summary>
    /// Initializes a newsletter scraper that proxies requests through Browserless.
    /// </summary>
    /// <param name="httpClient">HTTP client used to call Browserless.</param>
    /// <param name="browserless">Browserless endpoint configuration.</param>
    /// <param name="newsletterUrlValidator">Validator that approves or rejects newsletter URLs.</param>
    /// <param name="logger">Logger used for scraping diagnostics.</param>
    public NewsletterScraper(
        HttpClient httpClient,
        IOptions<BrowserlessOptions> browserless,
        INewsletterUrlValidator newsletterUrlValidator,
        ILogger<NewsletterScraper> logger)
    {
        _httpClient = httpClient;
        _browserless = browserless.Value;
        _newsletterUrlValidator = newsletterUrlValidator;
        _logger = logger;
    }

    /// <summary>
    /// Scrapes the newsletter URL and returns the body text content.
    /// </summary>
    public async Task<string?> ScrapeAsync(string url, CancellationToken ct = default)
    {
        var validation = await _newsletterUrlValidator.ValidateAsync(url, ct);
        if (!validation.Allowed)
        {
            _logger.LogWarning("Blocked newsletter URL {Url}. Reason: {Reason}", url, validation.Reason);
            return null;
        }

        _logger.LogInformation("Scraping newsletter URL: {Url}", url);

        var scrapeUrl = $"{_browserless.BaseUrl.TrimEnd('/')}/scrape";

        var requestBody = new
        {
            url,
            elements = new[] { new { selector = "body" } },
            gotoOptions = new { waitUntil = "networkidle2", timeout = 60000 }
        };

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, scrapeUrl);
            request.Content = JsonContent.Create(requestBody);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);

            // Navigate: data[0].results[0].text
            var text = doc.RootElement
                .GetProperty("data")[0]
                .GetProperty("results")[0]
                .GetProperty("text")
                .GetString();

            _logger.LogInformation("Successfully scraped {Length} chars from {Url}",
                text?.Length ?? 0, url);

            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scrape newsletter URL: {Url}", url);
            return null;
        }
    }
}
