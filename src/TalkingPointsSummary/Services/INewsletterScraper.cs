namespace TalkingPointsSummary.Services;

/// <summary>
/// Scrapes text content from newsletter URLs.
/// </summary>
public interface INewsletterScraper
{
    /// <summary>
    /// Scrapes a newsletter URL and returns extracted body text.
    /// </summary>
    /// <param name="url">Newsletter URL to scrape.</param>
    /// <param name="ct">Token used to cancel the scrape.</param>
    Task<string?> ScrapeAsync(string url, CancellationToken ct = default);
}
