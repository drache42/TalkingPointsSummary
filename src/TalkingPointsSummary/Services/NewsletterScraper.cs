using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TalkingPointsSummary.Configuration;

namespace TalkingPointsSummary.Services;

/// <summary>
/// Scrapes newsletter content via an external Browserless service.
/// </summary>
public class NewsletterScraper
{
    private readonly HttpClient _httpClient;
    private readonly AppSettings _settings;
    private readonly ILogger<NewsletterScraper> _logger;

    public NewsletterScraper(
        HttpClient httpClient,
        IOptions<AppSettings> settings,
        ILogger<NewsletterScraper> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Scrapes the newsletter URL and returns the body text content.
    /// </summary>
    public async Task<string?> ScrapeAsync(string url, CancellationToken ct = default)
    {
        _logger.LogInformation("Scraping newsletter URL: {Url}", url);

        var scrapeUrl = $"{_settings.BrowserlessUrl.TrimEnd('/')}/scrape";

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
