namespace TalkingPointsSummary.Services;

/// <summary>
/// Validates whether a newsletter URL is safe to scrape.
/// </summary>
public interface INewsletterUrlValidator
{
    /// <summary>
    /// Validates whether a URL may be scraped.
    /// </summary>
    /// <param name="url">URL to validate.</param>
    /// <param name="cancellationToken">Token used to cancel validation.</param>
    Task<NewsletterUrlValidationResult> ValidateAsync(string url, CancellationToken cancellationToken = default);
}