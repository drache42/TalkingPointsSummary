namespace TalkingPointsSummary.Services;

public interface INewsletterUrlValidator
{
    Task<NewsletterUrlValidationResult> ValidateAsync(string url, CancellationToken cancellationToken = default);
}