namespace TalkingPointsSummary.Services;

public interface INewsletterScraper
{
    Task<string?> ScrapeAsync(string url, CancellationToken ct = default);
}
